﻿using IPA.Utilities.Async;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Logger = IPA.Logging.Logger;

namespace IPA.Config
{
    internal static class ConfigRuntime
    {
        private class DirInfoEqComparer : IEqualityComparer<DirectoryInfo>
        {
            public bool Equals(DirectoryInfo x, DirectoryInfo y)
                => x?.FullName == y?.FullName;

            public int GetHashCode(DirectoryInfo obj)
                => obj?.FullName.GetHashCode() ?? 0;
        }

        private static readonly ConcurrentBag<Config> configs = new();
        private static readonly AutoResetEvent configsChangedWatcher = new(false);
        private static readonly ConcurrentDictionary<DirectoryInfo, FileSystemWatcher> watchers = new(new DirInfoEqComparer());
        private static readonly ConcurrentDictionary<FileSystemWatcher, ConcurrentBag<Config>> watcherTrackConfigs = new();
        private static BlockingCollection<IConfigStore> requiresSave = new();
        private static SingleThreadTaskScheduler loadScheduler;
        private static TaskFactory loadFactory;
        private static Thread saveThread;
        private static Thread legacySaveThread;

        private static void TryStartRuntime()
        {
            if (loadScheduler == null || !loadScheduler.IsRunning)
            {
                loadFactory = null;
                loadScheduler = new SingleThreadTaskScheduler();
                loadScheduler.Start();
            }
            if (loadFactory == null)
                loadFactory = new TaskFactory(loadScheduler);
            if (saveThread == null || !saveThread.IsAlive)
            {
                saveThread = new Thread(SaveThread);
                saveThread.Start();
            }
            if (legacySaveThread == null || !legacySaveThread.IsAlive)
            {
                legacySaveThread = new Thread(LegacySaveThread);
                legacySaveThread.Start();
            }

            AppDomain.CurrentDomain.ProcessExit -= ShutdownRuntime;
            AppDomain.CurrentDomain.ProcessExit += ShutdownRuntime;
        }

        internal static void AddRequiresSave(IConfigStore configStore)
        {
            requiresSave?.Add(configStore);
        }

        private static void ShutdownRuntime(object sender, EventArgs e)
            => ShutdownRuntime();
        internal static void ShutdownRuntime()
        {
            try
            {
                watcherTrackConfigs.Clear();
                var watchList = watchers.ToArray();
                watchers.Clear();

                foreach (var pair in watchList)
                    pair.Value.EnableRaisingEvents = false;

                loadScheduler.Join(); // we can wait for the loads to finish
                saveThread.Abort(); // eww, but i don't like any of the other potential solutions
                legacySaveThread.Abort();

                SaveAll();

                requiresSave.Dispose();
                requiresSave = null;
            }
            catch
            {
            }
        }

        public static void RegisterConfig(Config cfg)
        {
            lock (configs)
            { // we only lock this segment, so that this only waits on other calls to this
                if (configs.ToArray().Contains(cfg))
                    throw new InvalidOperationException("Config already registered to runtime!");

                configs.Add(cfg);
            }
            configsChangedWatcher.Set();

            TryStartRuntime();

            AddConfigToWatchers(cfg);
        }

        public static void ConfigChanged()
        {
            configsChangedWatcher.Set();
        }

        private static void AddConfigToWatchers(Config config)
        {
            var dir = config.File.Directory;
            if (!watchers.TryGetValue(dir, out var watcher))
            { // create the watcher
                watcher = watchers.GetOrAdd(dir, dir => new FileSystemWatcher(dir.FullName));

                watcher.NotifyFilter =
                    NotifyFilters.FileName
                    | NotifyFilters.LastWrite
                    | NotifyFilters.Size
                    | NotifyFilters.LastAccess
                    | NotifyFilters.Attributes
                    | NotifyFilters.CreationTime;

                watcher.Changed += FileChangedEvent;
                watcher.Created += FileChangedEvent;
                watcher.Renamed += FileChangedEvent;
                watcher.Deleted += FileChangedEvent;
            }

            TryStartRuntime();

            watcher.EnableRaisingEvents = false; // disable while we do shit

            var bag = watcherTrackConfigs.GetOrAdd(watcher, w => new ConcurrentBag<Config>());
            // we don't need to check containment because this function will only be called once per config ever
            bag.Add(config);

            watcher.EnableRaisingEvents = true;
        }

        internal static FileSystemWatcher[] GetWatchers()
        {
            return watcherTrackConfigs.Keys.ToArray();
        }

        private static void EnsureWritesSane(Config config)
        {
            // compare exchange loop to be sane
            var writes = config.Writes;
            while (writes < 0)
                writes = Interlocked.CompareExchange(ref config.Writes, 0, writes);
        }

        private static void FileChangedEvent(object sender, FileSystemEventArgs e)
        {
            var watcher = sender as FileSystemWatcher;
            if (!watcherTrackConfigs.TryGetValue(watcher, out var bag)) return;

            var config = bag.FirstOrDefault(c => c.File.FullName == e.FullPath);
            if (config != null && Interlocked.Decrement(ref config.Writes) + 1 <= 0)
            {
                EnsureWritesSane(config);
                TriggerFileLoad(config);
            }
        }

        public static Task TriggerFileLoad(Config config)
            => loadFactory.StartNew(() => LoadTask(config));

        public static Task TriggerLoadAll()
            => Task.WhenAll(configs.Select(TriggerFileLoad));

        /// <summary>
        /// this is synchronous, unlike <see cref="TriggerFileLoad(Config)"/>
        /// </summary>
        /// <param name="config"></param>
        public static void Save(Config config)
        {
            var store = config.Store;

            try
            {
                using var readLock = Synchronization.LockRead(store.WriteSyncObject);

                EnsureWritesSane(config);
                Interlocked.Increment(ref config.Writes);
                store.WriteTo(config.configProvider);
            }
            catch (ThreadAbortException)
            {
                throw;
            }
            catch (Exception e)
            {
                Logger.Config.Error($"{nameof(IConfigStore)} for {config.File} errored while writing to disk");
                Logger.Config.Error(e);
            }
        }

        /// <summary>
        /// this is synchronous, unlike <see cref="TriggerLoadAll"/>
        /// </summary>
        public static void SaveAll()
        {
            foreach (var config in configs)
                Save(config);
        }

        private static void LoadTask(Config config)
        { // these tasks will always be running in the same thread as each other
            try
            {
                var store = config.Store;
                using var writeLock = Synchronization.LockWrite(store.WriteSyncObject);
                store.ReadFrom(config.configProvider);
            }
            catch (Exception e)
            {
                Logger.Config.Error($"{nameof(IConfigStore)} for {config.File} errored while reading from the {nameof(IConfigProvider)}");
                Logger.Config.Error(e);
            }
        }

        private static void SaveThread()
        {
            if (requiresSave == null)
            {
                return;
            }

            try
            {
                foreach (var item in requiresSave.GetConsumingEnumerable())
                {
                    try
                    {
                        Save(configs.First((c) => c.Store != null && ReferenceEquals(c.Store.WriteSyncObject, item.WriteSyncObject)));
                    }
                    catch (ThreadAbortException)
                    {
                        break;
                    }
                    catch (Exception e)
                    {
                        Logger.Config.Error($"Error waiting for in-memory updates");
                        Logger.Config.Error(e);
                        Thread.Sleep(TimeSpan.FromSeconds(1));
                    }
                }
            }
            catch (ThreadAbortException)
            {
                // we got aborted :(
            }
        }

        private static void LegacySaveThread()
        {
            try
            {
                while (true)
                {
                    var configArr = configs.Where(c => c.Store?.SyncObject != null).ToArray();
                    int index = -1;
                    try
                    {
                        var waitHandles = configArr.Select(c => c.Store.SyncObject)
                                                 .Prepend(configsChangedWatcher)
                                                 .ToArray();
                        index = WaitHandle.WaitAny(waitHandles);
                    }
                    catch (ThreadAbortException)
                    {
                        break;
                    }
                    catch (Exception e)
                    {
                        Logger.Config.Error($"Error waiting for in-memory updates");
                        Logger.Config.Error(e);
                        Thread.Sleep(TimeSpan.FromSeconds(1));
                    }

                    if (index <= 0)
                    { // we got a signal that the configs collection changed, loop around, or errored
                        continue;
                    }

                    // otherwise, we have a thing that changed in a store
                    Save(configArr[index - 1]);
                }
            }
            catch (ThreadAbortException)
            {
                // we got aborted :(
            }
        }
    }
}

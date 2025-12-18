#nullable enable
using Microsoft.Win32;
using System;
using System.IO;
using System.Linq;

namespace IPA.Injector
{
    internal class OpenXRHelper
    {
        public static string? GetOpenXRRuntime()
        {
            var runtime = Environment.GetEnvironmentVariable("XR_RUNTIME_JSON");
            if (runtime != null)
            {
                return runtime;
            }

            using var baseKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Khronos\OpenXR\1");
            return baseKey?.GetValue("ActiveRuntime") as string;
        }

        public static void SetOpenXRRuntime(string targetRuntime)
        {
            // Forces invalid runtime to prevent OpenXR initialization.
            if (targetRuntime == "none")
            {
                Environment.SetEnvironmentVariable("XR_RUNTIME_JSON", targetRuntime);
                return;
            }

            using var baseKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Khronos\OpenXR\1\AvailableRuntimes");
            var foundRuntime = baseKey?.GetValueNames().FirstOrDefault(f => File.Exists(f) && Path.GetFileName(f).Contains(targetRuntime));
            if (foundRuntime != null)
            {
                Environment.SetEnvironmentVariable("XR_RUNTIME_JSON", foundRuntime);
            }
        }
    }
}
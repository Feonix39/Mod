﻿namespace XIVLauncher.Common
{
    public static class EnvironmentSettings
    {
        public static bool IsWine => CheckEnvBool("XL_WINEONLINUX");
        public static bool IsHardwareRendered => CheckEnvBool("XL_HWRENDER");
        public static bool IsDisableUpdates => CheckEnvBool("XL_NOAUTOUPDATE");
        public static bool IsPreRelease => CheckEnvBool("XL_PRERELEASE");
        public static bool IsNoRunas => CheckEnvBool("XL_NO_RUNAS");
        public static bool IsIgnoreSpaceRequirements => CheckEnvBool("XL_NO_SPACE_REQUIREMENTS");
        public static bool IsWineD3D => CheckEnvBool("XL_FORCE_WINED3D");
        private static bool CheckEnvBool(string var)
        {
            var = (System.Environment.GetEnvironmentVariable(var) ?? "false").ToLower();
            return (var.Equals("1") || var.Equals("true") || var.Equals("on") || var.Equals("yes"));
        }
    }
}
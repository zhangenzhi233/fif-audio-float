using System;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace FifFloat
{
    internal static class SystemAudio
    {
        // Windows' policy client is used only by this explicit repair command;
        // the floating recorder never changes system default audio endpoints.
        [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
        private class PolicyClient { }

        [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPolicyConfig
        {
            [PreserveSig] int GetMixFormat();
            [PreserveSig] int GetDeviceFormat();
            [PreserveSig] int ResetDeviceFormat();
            [PreserveSig] int SetDeviceFormat();
            [PreserveSig] int GetProcessingPeriod();
            [PreserveSig] int SetProcessingPeriod();
            [PreserveSig] int GetShareMode();
            [PreserveSig] int SetShareMode();
            [PreserveSig] int GetPropertyValue();
            [PreserveSig] int SetPropertyValue();
            [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, Role role);
            [PreserveSig] int SetEndpointVisibility();
        }

        public static string RestoreRealtekOutputIfCableIsDefault()
        {
            using (MMDeviceEnumerator enumerator = new MMDeviceEnumerator())
            {
                string targetId = null, targetName = null;
                foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    using (device)
                    {
                        if (device.FriendlyName.IndexOf("Realtek", StringComparison.OrdinalIgnoreCase) >= 0)
                        { targetId = device.ID; targetName = device.FriendlyName; break; }
                    }
                }
                if (targetId == null) throw new InvalidOperationException("没有找到活动的 Realtek 扬声器；未改变默认设备。");
                IPolicyConfig policy = (IPolicyConfig)new PolicyClient();
                try
                {
                    foreach (Role role in new[] { Role.Console, Role.Multimedia })
                    {
                        using (MMDevice current = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, role))
                            if (current.FriendlyName.StartsWith("CABLE", StringComparison.OrdinalIgnoreCase))
                                Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(targetId, role));
                    }
                }
                finally { Marshal.FinalReleaseComObject(policy); }
                return targetName;
            }
        }
    }
}

using System;
using System.Runtime.InteropServices;

namespace Hd820GameBridge.Reviewer
{
    // IPolicyConfig is an undocumented Windows COM interface. These two calls
    // are isolated so an unsupported Windows build fails with a visible HRESULT.
    // No audio formats, volume, mute, spatial audio or driver files are changed.
    // The caller must snapshot old endpoint IDs BEFORE changing any default role.
    // Restore a role only if its current ID still equals the ID set by this app.
    public static class AudioPolicy
    {
        public enum Role
        {
            Console = 0,
            Multimedia = 1,
            Communications = 2
        }

        public static void SetDefaultEndpoint(string endpointId, Role role)
        {
            ValidateEndpoint(endpointId);
            if ((int)role < 0 || (int)role > 2)
                throw new ArgumentOutOfRangeException("role");
            Invoke(delegate(IPolicyConfigWin7 config)
            {
                return config.SetDefaultEndpoint(endpointId, (int)role);
            });
        }

        public static void SetEndpointVisibility(string endpointId, bool visible)
        {
            ValidateEndpoint(endpointId);
            Invoke(delegate(IPolicyConfigWin7 config)
            {
                return config.SetEndpointVisibility(endpointId, visible);
            });
        }

        private static void ValidateEndpoint(string endpointId)
        {
            if (String.IsNullOrWhiteSpace(endpointId))
                throw new ArgumentException("An exact endpoint ID is required.", "endpointId");
        }

        private static void Invoke(Func<IPolicyConfigWin7, int> action)
        {
            // Create and release on the calling thread. Do not cache this RCW
            // across capture/render/UI thread apartments.
            object instance = null;
            try
            {
                instance = new PolicyConfigClient();
                IPolicyConfigWin7 config = (IPolicyConfigWin7)instance;
                int hr = action(config);
                if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            }
            finally
            {
                if (instance != null && Marshal.IsComObject(instance))
                    Marshal.ReleaseComObject(instance);
            }
        }

        [ComImport]
        [Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
        private class PolicyConfigClient
        {
        }

        [ComImport]
        [Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPolicyConfigWin7
        {
            // Slots 0..9 after IUnknown. Deliberately never invoked: signatures
            // vary in public reverse-engineered definitions. Get the mix format
            // through the documented IAudioClient.GetMixFormat instead.
            [PreserveSig] int UnusedGetMixFormat();
            [PreserveSig] int UnusedGetDeviceFormat();
            [PreserveSig] int UnusedResetDeviceFormat();
            [PreserveSig] int UnusedSetDeviceFormat();
            [PreserveSig] int UnusedGetProcessingPeriod();
            [PreserveSig] int UnusedSetProcessingPeriod();
            [PreserveSig] int UnusedGetShareMode();
            [PreserveSig] int UnusedSetShareMode();
            [PreserveSig] int UnusedGetPropertyValue();
            [PreserveSig] int UnusedSetPropertyValue();

            [PreserveSig]
            int SetDefaultEndpoint(
                [In, MarshalAs(UnmanagedType.LPWStr)] string endpointId,
                [In] int role);

            [PreserveSig]
            int SetEndpointVisibility(
                [In, MarshalAs(UnmanagedType.LPWStr)] string endpointId,
                [In, MarshalAs(UnmanagedType.Bool)] bool visible);
        }
    }
}

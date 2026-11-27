using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace VolumeMixer.Services;

internal static class PolicyConfigClient
{
    private static readonly Guid PolicyConfigClientId = new("870af99c-171d-4f9e-af0d-e63df40c2bc9");

    private static readonly Role[] AllRoles =
    [
        Role.Console,
        Role.Multimedia,
        Role.Communications
    ];

    public static void SetDefaultForAllRoles(string endpointId)
    {
        var type = Type.GetTypeFromCLSID(PolicyConfigClientId, throwOnError: true);
        var policyConfig = (IPolicyConfig)Activator.CreateInstance(type!)!;
        try
        {
            foreach (var role in AllRoles)
            {
                policyConfig.SetDefaultEndpoint(endpointId, role);
            }
        }
        finally
        {
            if (Marshal.IsComObject(policyConfig))
            {
                Marshal.ReleaseComObject(policyConfig);
            }
        }
    }

    [ComImport]
    [Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        void GetMixFormat();
        void GetDeviceFormat();
        void ResetDeviceFormat();
        void SetDeviceFormat();
        void GetProcessingPeriod();
        void SetProcessingPeriod();
        void GetShareMode();
        void SetShareMode();
        void GetPropertyValue();
        void SetPropertyValue();
        void SetDefaultEndpoint(
            [MarshalAs(UnmanagedType.LPWStr)] string wszDeviceId,
            Role role);
        void SetEndpointVisibility();
    }
}

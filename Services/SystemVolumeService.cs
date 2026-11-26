using NAudio.CoreAudioApi;

namespace InkCanvasForPDF.Services;

/// <summary>系统主输出音量控制（NAudio.Wasapi / WASAPI）。</summary>
public static class SystemVolumeService
{
    /// <summary>把默认播放设备的主音量调到 100%（标量 1.0）。失败写 error.log 并返回 false，不抛异常。</summary>
    public static bool SetMaxVolume()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            device.AudioEndpointVolume.Mute = false;   // 先解除静音，再拉音量
            device.AudioEndpointVolume.MasterVolumeLevelScalar = 1f;
            return true;
        }
        catch (Exception ex)
        {
            App.Log("调节音量", ex);
            return false;
        }
    }
}

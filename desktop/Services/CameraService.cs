using System.IO;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;

namespace Classify.Services;

/// <summary>
/// 摄像头拍照回传：优先使用名称含 "SmartCamera" 的摄像头（希沃一体机前置），
/// 未找到回退默认摄像头；仅教师触发时激活，拍完即释放。JPEG 回传教师文件列表。
/// 经 Windows SDK 投影调用 MediaCapture（v3 唯一的 WinRT 依赖面）。
/// </summary>
public static class CameraService
{
    public static async Task CaptureAndUploadAsync()
    {
        var devices = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(
            Windows.Devices.Enumeration.DeviceClass.VideoCapture);
        if (devices.Count == 0) throw new InvalidOperationException("本机没有可用摄像头");

        var selected = devices.FirstOrDefault(d =>
            d.Name.Contains("SmartCamera", StringComparison.OrdinalIgnoreCase))
            ?? devices[0];
        App.Log.Info($"拍照使用摄像头: {selected.Name}");

        string path = Path.Combine(Path.GetTempPath(),
            $"classify-cam-{DateTime.Now:yyyyMMdd-HHmmss}.jpg");

        var mc = new MediaCapture();
        try
        {
            await mc.InitializeAsync(new MediaCaptureInitializationSettings
            {
                VideoDeviceId = selected.Id,
                StreamingCaptureMode = StreamingCaptureMode.Video,
            });
            var props = ImageEncodingProperties.CreateJpeg();
            using var stream = new InMemoryRandomAccessStream();
            await mc.CapturePhotoToStreamAsync(props, stream);

            var size = (int)stream.Size;
            var buffer = new byte[size];
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)size);
            reader.ReadBytes(buffer);
            await File.WriteAllBytesAsync(path, buffer);
        }
        finally
        {
            mc.Dispose();
        }

        var newName = $"教室照片-{DateTime.Now:yyyyMMdd-HHmmss}.jpg";
        await new ApiService().UploadToTeacherAsync(path, newName);
        try { File.Delete(path); } catch { }
    }
}

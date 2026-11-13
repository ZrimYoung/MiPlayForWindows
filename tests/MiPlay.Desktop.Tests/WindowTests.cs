using System.Net;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DLNACast.Core.Abstractions;
using DLNACast.Core.MiPlay;
using DLNACast.Core.Models;
using MiPlay.Desktop.Services;

[assembly: AvaloniaTestApplication(typeof(MiPlay.Desktop.Tests.TestAppBuilder))]

namespace MiPlay.Desktop.Tests;

public sealed class TestApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
}
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApp>()
        .UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public sealed class WindowTests
{
    [AvaloniaFact]
    public async Task DeviceSelectionAndStartStopBindingsWorkWithoutCapturingAudio()
    {
        var directory = Directory.CreateTempSubdirectory("miplay-ui-");
        try
        {
            using var platform = new NoAudioPlatform();
            var discovery = new PreviewDiscovery();
            var session = new PreviewSession();
            var service = new CastService(platform, discovery, () => session, () => "test");
            await using var vm = new MainViewModel(service, discovery, new DesktopStore(directory.FullName));
            var window = new MainWindow { DataContext = vm };
            try
            {
                window.Show();
                await vm.RefreshAsync();
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(2, vm.Speakers.Count);
                Assert.False(vm.StartCommand.CanExecute(null));
                var picker = window.GetVisualDescendants().OfType<ComboBox>().Single();
                picker.SelectedIndex = 0;
                Dispatcher.UIThread.RunJobs();
                Assert.Equal("小爱音箱 Art", vm.SelectedSpeaker!.Name);
                Assert.True(vm.StartCommand.CanExecute(null));
                var screenshot = Environment.GetEnvironmentVariable("MIPLAY_UI_SNAPSHOT");
                if (!string.IsNullOrEmpty(screenshot))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(screenshot)!);
                    using var bitmap = window.CaptureRenderedFrame();
                    Assert.NotNull(bitmap);
                    bitmap.Save(screenshot, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
                }
                vm.StartCommand.Execute(null);
                await WaitFor(() => vm.IsCasting);
                Assert.False(picker.IsEnabled);
                Assert.True(vm.StopCommand.CanExecute(null));
                await vm.StopAsync();
                await WaitFor(() => vm.CanSelectSpeaker);
                Assert.True(session.Disposed);
                Assert.True(picker.IsEnabled);
            }
            finally { window.Close(); }
        }
        finally { directory.Delete(true); }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10, timeout.Token); }
    }
    private sealed class PreviewDiscovery : ISpeakerDiscovery
    {
        public Task<IReadOnlyList<Speaker>> SearchAsync(CancellationToken token)
        {
            var uri = new Uri("http://192.0.2.1:8899/");
            var endpoint = new UpnpServiceEndpoint("", uri, uri, uri);
            Speaker Make(string name) => new(name, name, "", new(name, name, "Xiaomi", "", IPAddress.Parse("192.0.2.1"), uri, endpoint, endpoint, endpoint, ""));
            return Task.FromResult<IReadOnlyList<Speaker>>([Make("小爱音箱 Art"), Make("Xiaomi 智能音箱 Pro")]);
        }
    }
    private sealed class NoAudioPlatform : IDesktopAudioPlatform
    {
        public event EventHandler? Suspending { add { } remove { } }
        public AudioSourceItem GetDefaultOutputDevice() => new("test", "default");
        public IReadOnlyList<AudioSourceItem> GetOutputDevices() => [];
        public IReadOnlyList<AudioSourceItem> GetCandidateProcesses() => [];
        public IAudioCaptureSource CreateCapture(CaptureSelection selection) => throw new InvalidOperationException("UI tests must not capture audio.");
        public IMediaPacingClock CreateMediaClock() => new MonotonicMediaPacingClock();
        public void Dispose() { }
    }
    private sealed class PreviewSession : ICastSession
    {
        public bool Disposed;
        public MiPlayCastDiagnostics Diagnostics { get; private set; } = new(MiPlayCastState.Idle, "");
        public event EventHandler<MiPlayCastDiagnostics>? DiagnosticsChanged;
        public Task StartAsync(MiPlaySystemAudioRequest request, CancellationToken token)
        {
            Diagnostics = new(MiPlayCastState.Streaming, ""); DiagnosticsChanged?.Invoke(this, Diagnostics); return Task.CompletedTask;
        }
        public Task StopAsync() => Task.CompletedTask;
        public Task SetVolumeAsync(int volume, CancellationToken token) => Task.CompletedTask;
        public Task SetCaptureAsync(CaptureSelection selection, CancellationToken token) => Task.CompletedTask;
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}

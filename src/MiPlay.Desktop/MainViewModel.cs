using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia.Threading;
using MiPlay.Desktop.Services;

namespace MiPlay.Desktop;

public sealed class MainViewModel : ObservableModel, IAsyncDisposable
{
    private readonly DesktopController controller;
    private bool disposed, mute;
    private bool synchronizedPlayback;
    private int audioQualityIndex;
    public string[] AudioQualities { get; } = ["256 kbps · 标准", "320 kbps · 高音质", "512 kbps · 实验性"];
    public int AudioQualityIndex
    {
        get => audioQualityIndex;
        set { if (value is < 0 or > 2 || value == audioQualityIndex) return; audioQualityIndex = value; Notify(); _ = ChangeQualityAsync(value); }
    }
    private async Task ChangeQualityAsync(int index)
    {
        try { await controller.SetAudioBitRateAsync(new[] {256000, 320000, 512000}[index]); }
        catch (Exception ex) { ReportError(ex); Apply(controller.Snapshot); }
    }
    private long revision;
    private string discoveryHint = "正在查找附近音箱…", error = "";
    public MainViewModel(DesktopController controller)
    {
        this.controller = controller;
        RefreshCommand = new(() => controller.RefreshAsync(), () => true, ReportError);
        StopCommand = new(StopAsync, () => Speakers.Any(s => s.Selected), ReportError);
        ExportCommand = new(() => { ErrorText = $"诊断已导出：{controller.Export()}"; return Task.CompletedTask; }, () => true, ReportError);
        PermissionCommand = new(() => { Process.Start(new ProcessStartInfo("x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture") { UseShellExecute = true }); return Task.CompletedTask; }, () => IsMac, ReportError);
        controller.Changed += OnChanged;
        Apply(controller.Snapshot);
    }
    public ObservableCollection<SpeakerViewModel> Speakers { get; } = [];
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand StopCommand { get; }
    public AsyncCommand ExportCommand { get; }
    public AsyncCommand PermissionCommand { get; }
    public bool IsMac => OperatingSystem.IsMacOS();
    public bool SupportsLocalMute => controller.Snapshot.SupportsLocalMute;
    public bool IsEmpty => Speakers.Count == 0;
    public string StatusText => Speakers.Any(s => s.Selected) ? $"正在投放到 {Speakers.Count(s => s.Selected)} 台音箱" : "选择音箱，让声音随你而行";
    public string DiscoveryHint { get => discoveryHint; private set { discoveryHint = value; Notify(); } }
    public string ErrorText { get => error; private set { error = value; Notify(); Notify(nameof(HasError)); } }
    public bool HasError => error.Length > 0;
    public bool MuteLocalOutput
    {
        get => mute;
        set { if (mute == value) return; mute = value; Notify(); _ = ChangeMuteAsync(value); }
    }
    public string LocalOutputHint => SupportsLocalMute ? "捕获声音后静音本机，停止全部投放后恢复。" : "Windows 暂不支持静音本机，电脑将同时出声。";
    public bool SynchronizedPlayback
    {
        get => synchronizedPlayback;
        set { if (synchronizedPlayback == value) return; synchronizedPlayback = value; Notify(); _ = ChangeModeAsync(value); }
    }
    private async Task ChangeModeAsync(bool value)
    {
        try { await controller.SetSynchronizedPlaybackAsync(value); }
        catch (Exception ex) { ReportError(ex); synchronizedPlayback = controller.Snapshot.SynchronizedPlayback; Notify(nameof(SynchronizedPlayback)); }
    }
    private async Task ChangeMuteAsync(bool value)
    {
        try { await controller.SetMutedAsync(value); }
        catch (Exception ex) { ReportError(ex); mute = controller.Snapshot.MuteLocalOutput; Notify(nameof(MuteLocalOutput)); }
    }
    public void StartDiscovery() => controller.StartDiscovery();
    public Task RefreshAsync() => controller.RefreshAsync();
    public Task StopAsync() => controller.StopAllAsync();
    private void OnChanged(object? sender, DesktopSnapshot snapshot) => Dispatcher.UIThread.Post(() => { if (!disposed) Apply(snapshot); });
    private void Apply(DesktopSnapshot snapshot)
    {
        if (snapshot.Revision < revision) return;
        revision = snapshot.Revision;
        audioQualityIndex = snapshot.AudioBitRate switch { 320000 => 1, 512000 => 2, _ => 0 }; Notify(nameof(AudioQualityIndex));
        foreach (var card in snapshot.Speakers)
        {
            var vm = Speakers.FirstOrDefault(s => s.Id == card.Id);
            if (vm is null) { vm = new(controller, card, ReportError); Speakers.Add(vm); }
            vm.Update(card);
        }
        mute = snapshot.MuteLocalOutput; Notify(nameof(MuteLocalOutput));
        synchronizedPlayback = snapshot.SynchronizedPlayback; Notify(nameof(SynchronizedPlayback));
        DiscoveryHint = snapshot.DiscoveryMessage;
        Notify(nameof(IsEmpty)); Notify(nameof(StatusText)); StopCommand.Refresh();
    }
    private void ReportError(Exception ex) => Dispatcher.UIThread.Post(() => ErrorText = ex.Message);
    public async ValueTask DisposeAsync()
    {
        disposed = true; controller.Changed -= OnChanged;
        foreach (var speaker in Speakers) await speaker.DisposeAsync();
        await controller.DisposeAsync();
    }
}

public sealed class SpeakerViewModel : ObservableModel, IAsyncDisposable
{
    private readonly DesktopController controller;
    private readonly Action<Exception> error;
    private SpeakerCard card;
    private double volume, delay;
    private bool delayDirty, updating;
    private CancellationTokenSource? volumeChange;
    private Task volumeTask = Task.CompletedTask;
    private Task delayTask = Task.CompletedTask;
    private bool disposed;
    public SpeakerViewModel(DesktopController controller, SpeakerCard card, Action<Exception> error)
    {
        this.controller = controller; this.card = card; this.error = error;
        volume = card.Volume ?? 30; delay = card.DelayMilliseconds;
        ToggleCommand = new(() => controller.SetSelectedAsync(Id, !Selected), () => card.Online || Selected, error, allowConcurrent: true);
        FastCommand = new(() => SetPreset(200), () => true, error);
        BalancedCommand = new(() => SetPreset(500), () => true, error);
        StableCommand = new(() => SetPreset(1000), () => true, error);
        ToggleDelayCommand = new(() => { Expanded = !Expanded; return Task.CompletedTask; }, () => true, error);
    }
    public string Id => card.Id;
    public string Name => card.Name;
    public bool HasGroup => !string.IsNullOrWhiteSpace(card.GroupId);
    public string GroupLabel => HasGroup ? $"{card.GroupName} · 立体声组合 · {card.Channel switch { "left" => "左声道", "right" => "右声道", _ => "组合成员" }}" : "";
    public bool Selected => card.Selected;
    public string StateText => !card.Online && !Selected ? "离线 · 保留上次设置" : card.State == "Idle" ? "点选加入投放" : card.Message;
    public bool IsError => card.State == "Error";
    public AsyncCommand ToggleCommand { get; }
    public AsyncCommand FastCommand { get; }
    public AsyncCommand BalancedCommand { get; }
    public AsyncCommand StableCommand { get; }
    public AsyncCommand ToggleDelayCommand { get; }
    private bool expanded;
    public bool Expanded { get => expanded; set { expanded = value; Notify(); Notify(nameof(ExpandLabel)); } }
    public string ExpandLabel => expanded ? "收起" : "调节";
    public string VolumeLabel => card.Volume is null && volumeChange is null ? "拖动设置音量" : $"{Math.Round(volume)}%";
    public double Volume
    {
        get => volume;
        set
        {
            if (Math.Abs(value - volume) < .5) return;
            volume = value; Notify(); Notify(nameof(VolumeLabel));
            if (updating) return;
            volumeChange?.Cancel();
            var change = new CancellationTokenSource(); volumeChange = change;
            volumeTask = SendVolumeAsync((int)Math.Round(value), change);
        }
    }
    public double Delay
    {
        get => delay;
        set
        {
            var rounded = Math.Round(value / 10) * 10;
            if (delay == rounded) return;
            delay = rounded; delayDirty = true; Notify(); Notify(nameof(DelayLabel));
            if (delayTask.IsCompleted) delayTask = SendDelayAsync();
        }
    }
    public string DelayLabel => $"{delay:0} ms";
    private Task SetPreset(int value) { Delay = value; return delayTask; }
    private async Task SendDelayAsync()
    {
        try
        {
            while (!disposed)
            {
                await Task.Delay(120);
                if (disposed) return;
                var target = (int)delay;
                if (HasGroup) await controller.SetGroupDelayAsync(card.GroupId!, target);
                else await controller.SetDelayAsync(Id, target);
                if ((int)delay == target) return;
            }
        }
        catch (Exception ex) { error(ex); }
    }
    private async Task SendVolumeAsync(int value, CancellationTokenSource change)
    {
        try { await Task.Delay(180, change.Token); await controller.SetVolumeAsync(Id, value); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { error(ex); }
        finally { if (ReferenceEquals(volumeChange, change)) volumeChange = null; change.Dispose(); }
    }
    public void Update(SpeakerCard value)
    {
        card = value;
        if (volumeChange is null && card.Volume is { } actual) { updating = true; Volume = actual; updating = false; }
        if (delay == card.DelayMilliseconds) delayDirty = false;
        if (!delayDirty) { delay = card.DelayMilliseconds; Notify(nameof(Delay)); Notify(nameof(DelayLabel)); }
        Notify(nameof(Name)); Notify(nameof(Selected)); Notify(nameof(StateText)); Notify(nameof(IsError)); Notify(nameof(VolumeLabel));
        Notify(nameof(GroupLabel)); Notify(nameof(HasGroup));
        ToggleCommand.Refresh();
    }
    public async ValueTask DisposeAsync() { disposed = true; volumeChange?.Cancel(); await Task.WhenAll(volumeTask, delayTask); }
}

public abstract class ObservableModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
public sealed class AsyncCommand(Func<Task> execute, Func<bool> canExecute, Action<Exception> onError, bool allowConcurrent = false) : ICommand
{
    private bool running;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => (allowConcurrent || !running) && canExecute();
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        running = true; Refresh();
        try { await execute(); } catch (Exception exception) { onError(exception); }
        finally { running = false; Refresh(); }
    }
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

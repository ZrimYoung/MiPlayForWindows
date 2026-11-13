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
        foreach (var card in snapshot.Speakers)
        {
            var vm = Speakers.FirstOrDefault(s => s.Id == card.Id);
            if (vm is null) { vm = new(controller, card, ReportError); Speakers.Add(vm); }
            vm.Update(card);
        }
        mute = snapshot.MuteLocalOutput; Notify(nameof(MuteLocalOutput));
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
    public SpeakerViewModel(DesktopController controller, SpeakerCard card, Action<Exception> error)
    {
        this.controller = controller; this.card = card; this.error = error;
        volume = card.Volume ?? 30; delay = card.DelayMilliseconds;
        ToggleCommand = new(() => controller.SetSelectedAsync(Id, !Selected), () => card.Online || Selected, error, allowConcurrent: true);
        ApplyDelayCommand = new(() => controller.SetDelayAsync(Id, (int)Math.Round(delay)), () => true, error);
        FastCommand = new(() => SetPreset(200), () => true, error);
        BalancedCommand = new(() => SetPreset(500), () => true, error);
        StableCommand = new(() => SetPreset(1000), () => true, error);
        ToggleDelayCommand = new(() => { Expanded = !Expanded; return Task.CompletedTask; }, () => true, error);
    }
    public string Id => card.Id;
    public string Name => card.Name;
    public bool Selected => card.Selected;
    public string StateText => !card.Online && !Selected ? "离线 · 保留上次设置" : card.State == "Idle" ? "点选加入投放" : card.Message;
    public bool IsError => card.State == "Error";
    public AsyncCommand ToggleCommand { get; }
    public AsyncCommand ApplyDelayCommand { get; }
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
        set { delay = Math.Round(value / 10) * 10; delayDirty = true; Notify(); Notify(nameof(DelayLabel)); }
    }
    public string DelayLabel => $"{delay:0} ms";
    private async Task SetPreset(int value) { Delay = value; await controller.SetDelayAsync(Id, value); }
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
        ToggleCommand.Refresh();
    }
    public async ValueTask DisposeAsync() { volumeChange?.Cancel(); await volumeTask; }
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

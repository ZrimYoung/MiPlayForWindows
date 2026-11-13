using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia.Threading;
using MiPlay.Desktop.Services;

namespace MiPlay.Desktop;

public sealed class MainViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly CastService service;
    private readonly ISpeakerDiscovery discovery;
    private readonly DesktopStore store;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private Speaker? selected;
    private string? lastSpeakerId;
    private bool refreshing;
    private string statusText = "选择音箱后开始投放";
    private string errorText = "";
    private string discoveryHint = "电脑与音箱需要连接到同一局域网。";
    private double volume = 50;
    private bool volumeKnown;
    private CancellationTokenSource? volumeChange;
    private Task volumeTask = Task.CompletedTask;
    private CastStatus current = new(DesktopCastState.Idle, "");

    public MainViewModel(CastService service, ISpeakerDiscovery discovery, DesktopStore store)
    {
        this.service = service; this.discovery = discovery; this.store = store;
        lastSpeakerId = store.Load().LastSpeakerId;
        RefreshCommand = new AsyncCommand(RefreshAsync, () => !refreshing, ReportError);
        StartCommand = new AsyncCommand(StartAsync, () => CanSelectSpeaker && SelectedSpeaker is not null, ReportError);
        StopCommand = new AsyncCommand(StopAsync, () => !CanSelectSpeaker, ReportError);
        ExportCommand = new AsyncCommand(ExportAsync, () => true, ReportError);
        PermissionCommand = new AsyncCommand(OpenPermissionAsync, () => IsMac, ReportError);
        service.StatusChanged += OnStatusChanged;
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<Speaker> Speakers { get; } = [];
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand StartCommand { get; }
    public AsyncCommand StopCommand { get; }
    public AsyncCommand ExportCommand { get; }
    public AsyncCommand PermissionCommand { get; }
    public bool IsMac => OperatingSystem.IsMacOS();
    public bool IsCasting => current.State == DesktopCastState.Casting;
    public bool CanSelectSpeaker => current.State is DesktopCastState.Idle or DesktopCastState.Error;
    public string StatusText { get => statusText; private set { statusText = value; Notify(); } }
    public string ErrorText { get => errorText; private set { errorText = value; Notify(); Notify(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrEmpty(ErrorText);
    public string DiscoveryHint { get => discoveryHint; private set { discoveryHint = value; Notify(); } }
    public Speaker? SelectedSpeaker
    {
        get => selected;
        set
        {
            selected = value; Notify(); StartCommand.Refresh();
            if (value is null) return;
            if (current.State == DesktopCastState.Idle) StatusText = $"已选择 {value.Name}，可以开始投放。";
            lastSpeakerId = value.Id;
            try { store.Save(new(value.Id)); } catch (Exception ex) { ReportError(ex); }
        }
    }
    public double Volume
    {
        get => volume;
        set
        {
            if (Math.Abs(volume - value) < .5) return;
            volume = value; volumeKnown = true; Notify(); Notify(nameof(VolumeLabel));
            volumeChange?.Cancel();
            var previous = volumeChange;
            volumeChange = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            volumeTask = ChangeVolumeAsync((int)Math.Round(value), volumeChange, previous);
        }
    }
    public string VolumeLabel => volumeKnown ? Math.Round(volume).ToString() : "—";
    private async Task ChangeVolumeAsync(int value, CancellationTokenSource change, CancellationTokenSource? previous)
    {
        try
        {
            await Task.Delay(180, change.Token);
            await service.SetVolumeAsync(value, change.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (!lifetime.IsCancellationRequested) ReportError(exception); }
        finally { previous?.Dispose(); }
    }
    public async Task RefreshAsync()
    {
        if (!await refreshGate.WaitAsync(0)) return;
        refreshing = true; RefreshCommand.Refresh();
        try
        {
            DiscoveryHint = "正在查找局域网中的妙播音箱…";
            var found = await discovery.SearchAsync(lifetime.Token);
            var identity = selected?.Id ?? lastSpeakerId;
            Speakers.Clear();
            foreach (var speaker in found) Speakers.Add(speaker);
            SelectedSpeaker = found.FirstOrDefault(s => s.Id == identity);
            DiscoveryHint = found.Count == 0
                ? "未发现妙播音箱。请检查同一局域网、设备电源及防火墙，再点击刷新。"
                : $"发现 {found.Count} 台音箱。";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { ReportError(ex); DiscoveryHint = "发现失败，可检查网络后重试。"; }
        finally { refreshing = false; RefreshCommand.Refresh(); refreshGate.Release(); }
    }
    private async Task StartAsync()
    {
        if (selected is null) return;
        ErrorText = "";
        try { await service.StartAsync(selected, lifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { store.Log("start", ex.Message); }
    }
    public async Task StopAsync()
    {
        volumeChange?.Cancel();
        try { await service.StopAsync(); } catch (Exception ex) { ReportError(ex); }
    }
    private Task ExportAsync()
    {
        var path = store.Export(selected);
        StatusText = $"诊断已导出到桌面：{Path.GetFileName(path)}";
        return Task.CompletedTask;
    }
    private Task OpenPermissionAsync()
    {
        Process.Start(new ProcessStartInfo("x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture") { UseShellExecute = true });
        return Task.CompletedTask;
    }
    private void OnStatusChanged(object? sender, CastStatus status)
    {
        store.Log("session", status.Message, status);
        Dispatcher.UIThread.Post(() =>
        {
            if (lifetime.IsCancellationRequested) return;
            current = status;
            StatusText = status.Message;
            ErrorText = status.State == DesktopCastState.Error ? status.Message : "";
            if (service.ReceiverVolume is { } actual && (volumeChange is null || volumeTask.IsCompleted))
            {
                volume = actual; volumeKnown = true; Notify(nameof(Volume)); Notify(nameof(VolumeLabel));
            }
            Notify(nameof(IsCasting)); Notify(nameof(CanSelectSpeaker));
            StartCommand.Refresh(); StopCommand.Refresh();
        });
    }
    private void ReportError(Exception exception)
    {
        store.Log("error", exception.Message);
        Dispatcher.UIThread.Post(() => ErrorText = exception.Message);
    }
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel(); volumeChange?.Cancel();
        service.StatusChanged -= OnStatusChanged;
        await service.DisposeAsync();
        await volumeTask;
        await refreshGate.WaitAsync(); refreshGate.Release();
        volumeChange?.Dispose(); lifetime.Dispose();
    }
}

public sealed class AsyncCommand(Func<Task> execute, Func<bool> canExecute, Action<Exception> onError) : ICommand
{
    private bool running;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !running && canExecute();
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        running = true; Refresh();
        try { await execute(); } catch (Exception exception) { onError(exception); }
        finally { running = false; Refresh(); }
    }
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

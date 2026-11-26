using System.Collections.ObjectModel;
using System.Globalization;
using FlyBrain.Modules.Mvvm;
using Trader.Brain;
using Trader.Core;
using Trader.Market;
using Trader.Module.Controls;

namespace Trader.Module.ViewModels;

public sealed record StrategyResultRow(
    string Strategy,
    string FinalEquity,
    string ReturnPercent,
    string MaxDrawdownPercent,
    int Trades,
    string Fees,
    string Signals);

/// <summary>Trader screen: load the 2024 replay, run baselines and the fly, watch a replay.</summary>
public sealed class TraderViewModel : ObservableObject, IAsyncDisposable
{
    public const int RandomSeed = 20260924;
    private const int MaxChartPoints = 1200;
    private const int MaxLogLines = 300;

    private static readonly IReadOnlyDictionary<string, string> Colors = new Dictionary<string, string>
    {
        ["Fly brain"] = "#65D6FF",
        ["Buy & hold"] = "#F2C14E",
        ["Momentum (trend token)"] = "#5CDB95",
    };

    private readonly ITraderBrain _brain;
    private readonly Func<MarketDataSet> _loadData;
    private readonly BacktestSettings _settings;
    private readonly MarketFeatureExtractor _extractor = new();
    private readonly Backtester _backtester = new();
    private IReadOnlyList<Candle> _candles = [];
    private IReadOnlyList<MarketSnapshot> _snapshots = [];
    private CancellationTokenSource? _cancellation;
    private bool _isDataLoaded, _isRunning;
    private string _dataStatus = "Market data not loaded.", _brainState = "OFFLINE", _brainDetail = "Start the brain to let the fly trade.";
    private string _statusText = "Idle", _currentTime = "-", _currentTokens = "-", _currentDecision = "-";
    private double _progress, _buyScore, _sellScore, _currentPrice, _brainEquity;
    private int _replayDelayMilliseconds = 30, _cacheSize;
    private IReadOnlyList<ChartSeries> _priceSeries = [], _equitySeries = [];

    public TraderViewModel(ITraderBrain brain, Func<MarketDataSet> loadData, string dataDirectory, BacktestSettings? settings = null)
    {
        _brain = brain;
        _loadData = loadData;
        _settings = settings ?? new BacktestSettings();
        DataDirectory = dataDirectory;
        LoadDataCommand = new AsyncCommand(LoadDataAsync, () => !IsRunning);
        RunBacktestCommand = new AsyncCommand(RunBacktestAsync, () => !IsRunning && IsDataLoaded);
        ReplayCommand = new AsyncCommand(ReplayAsync, () => !IsRunning && IsDataLoaded && IsBrainConnected);
        StopCommand = new AsyncCommand(StopAsync, () => IsRunning);
    }

    public string DataDirectory { get; }

    public string InitialCashText => _settings.InitialCash.ToString("N0", CultureInfo.InvariantCulture) + " USDT";

    public string FeeText => (_settings.FeeRate * 100).ToString("0.###", CultureInfo.InvariantCulture) + " %";

    public ObservableCollection<StrategyResultRow> Results { get; } = [];

    public ObservableCollection<string> Log { get; } = [];

    public AsyncCommand LoadDataCommand { get; }

    public AsyncCommand RunBacktestCommand { get; }

    public AsyncCommand ReplayCommand { get; }

    public AsyncCommand StopCommand { get; }

    public bool IsDataLoaded { get => _isDataLoaded; private set { if (Set(ref _isDataLoaded, value)) RefreshCommands(); } }

    public bool IsRunning { get => _isRunning; private set { if (Set(ref _isRunning, value)) RefreshCommands(); } }

    public bool IsBrainConnected => _brain.IsConnected;

    public string DataStatus { get => _dataStatus; private set => Set(ref _dataStatus, value); }

    public string BrainState { get => _brainState; private set => Set(ref _brainState, value); }

    public string BrainDetail { get => _brainDetail; private set => Set(ref _brainDetail, value); }

    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    public double Progress { get => _progress; private set => Set(ref _progress, value); }

    public int ReplayDelayMilliseconds { get => _replayDelayMilliseconds; set => Set(ref _replayDelayMilliseconds, Math.Clamp(value, 0, 1000)); }

    public string CurrentTime { get => _currentTime; private set => Set(ref _currentTime, value); }

    public double CurrentPrice { get => _currentPrice; private set => Set(ref _currentPrice, value); }

    public string CurrentTokens { get => _currentTokens; private set => Set(ref _currentTokens, value); }

    public string CurrentDecision { get => _currentDecision; private set => Set(ref _currentDecision, value); }

    public double BuyScore { get => _buyScore; private set => Set(ref _buyScore, value); }

    public double SellScore { get => _sellScore; private set => Set(ref _sellScore, value); }

    public double BrainEquity { get => _brainEquity; private set => Set(ref _brainEquity, value); }

    public int CacheSize { get => _cacheSize; private set => Set(ref _cacheSize, value); }

    public IReadOnlyList<ChartSeries> PriceSeries { get => _priceSeries; private set => Set(ref _priceSeries, value); }

    public IReadOnlyList<ChartSeries> EquitySeries { get => _equitySeries; private set => Set(ref _equitySeries, value); }

    public int SnapshotCount => _snapshots.Count;

    public async Task LoadDataAsync()
    {
        StatusText = "Loading market data...";
        try
        {
            var set = await Task.Run(_loadData);
            var snapshots = await Task.Run(() => _extractor.Extract(set.Candles));
            _candles = set.Candles;
            _snapshots = snapshots;
            var checksums = set.Files.Count(f => f.ChecksumValid is not null);
            var validChecksums = set.Files.Count(f => f.ChecksumValid == true);
            DataStatus = string.Create(CultureInfo.InvariantCulture,
                $"{set.Symbol} {set.Interval}: {set.Candles.Count:N0} candles, {set.Start:yyyy-MM-dd} to {set.End:yyyy-MM-dd}, {set.Files.Count} files, checksums {validChecksums}/{checksums} OK, gaps {set.GapCount}. {snapshots.Count:N0} tradable hours after warm-up.");
            if (checksums > validChecksums)
            {
                AddLog("Warning: some market files failed their checksum.");
            }

            PriceSeries = [new ChartSeries("BTC/USDT close", "#F2C14E", Downsample(_candles.Select(c => (double)c.Close).ToArray()))];
            IsDataLoaded = snapshots.Count > 0;
            StatusText = IsDataLoaded ? "Market data ready." : "Not enough candles for the 30-day warm-up.";
            Raise(nameof(SnapshotCount));
        }
        catch (Exception exception)
        {
            IsDataLoaded = false;
            DataStatus = $"No market data: {exception.Message}";
            StatusText = $"Put Binance BTCUSDT 1h kline zips into {DataDirectory}.";
        }
    }

    public async Task ConnectBrainAsync()
    {
        BrainState = "CONNECTING";
        BrainDetail = "Creating the trader session and selecting input neurons...";
        try
        {
            var status = await _brain.ConnectAsync(new Progress<string>(AddLog));
            BrainState = status.IsConnected ? "READY" : "OFFLINE";
            BrainDetail = status.IsConnected
                ? $"Inputs: {status.Layout!.InputRoots.Count} neurons · readout: {status.Layout.BuyPool.Count} BUY / {status.Layout.SellPool.Count} SELL"
                : status.Detail;
        }
        catch (Exception exception)
        {
            BrainState = "ERROR";
            BrainDetail = exception.Message;
            AddLog($"Trader brain connect failed: {exception.Message}");
        }

        Raise(nameof(IsBrainConnected));
        RefreshCommands();
    }

    public async Task ReleaseBrainAsync(string reason)
    {
        _cancellation?.Cancel();
        await _brain.ReleaseAsync();
        BrainState = "OFFLINE";
        BrainDetail = reason;
        Raise(nameof(IsBrainConnected));
        RefreshCommands();
    }

    public async Task RunBacktestAsync()
    {
        if (!IsDataLoaded)
        {
            return;
        }

        using var cancellation = BeginRun("Running backtests...");
        try
        {
            Results.Clear();
            var results = new List<BacktestResult>();
            var strategies = new List<ITradingStrategy> { new BuyAndHoldStrategy(), new MomentumStrategy(), new RandomStrategy(RandomSeed) };
            foreach (var strategy in strategies)
            {
                StatusText = $"Backtesting {strategy.Name}...";
                var result = await Task.Run(() => _backtester.RunAsync(_candles, _snapshots, strategy, _settings, cancellationToken: cancellation.Token));
                results.Add(result);
                Results.Add(ToRow(result));
            }

            if (IsBrainConnected)
            {
                StatusText = "Backtesting the fly brain (new market states are sent to the brain)...";
                var brain = CreateBrainStrategy();
                var result = await _backtester.RunAsync(_candles, _snapshots, brain, _settings, onStep: ReportBacktestStepAsync, cancellationToken: cancellation.Token);
                results.Insert(0, result);
                Results.Insert(0, ToRow(result));
            }
            else
            {
                AddLog("Fly brain skipped: start the brain to include it.");
            }

            EquitySeries = results.Select(ToSeries).ToArray();
            StatusText = cancellation.IsCancellationRequested ? "Backtest stopped." : "Backtest complete.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Backtest stopped.";
        }
        catch (Exception exception)
        {
            StatusText = $"Backtest failed: {exception.Message}";
            AddLog(StatusText);
        }
        finally
        {
            EndRun();
        }
    }

    public async Task ReplayAsync()
    {
        if (!IsDataLoaded || !IsBrainConnected)
        {
            return;
        }

        using var cancellation = BeginRun("Replaying 2024 with the fly...");
        try
        {
            var buyAndHold = await Task.Run(() => _backtester.RunAsync(_candles, _snapshots, new BuyAndHoldStrategy(), _settings));
            var reference = Downsample(buyAndHold.Equity.Select(p => (double)p.Equity).ToArray());
            var brainValues = new List<double>(_snapshots.Count);
            var brain = CreateBrainStrategy();
            var result = await _backtester.RunAsync(
                _candles,
                _snapshots,
                brain,
                _settings,
                onStep: async (step, token) =>
                {
                    brainValues.Add((double)step.Equity);
                    UpdateCurrent(step);
                    if (step.Step % 12 == 0 || step.Step == step.TotalSteps - 1)
                    {
                        var stride = Math.Max(1, (int)Math.Ceiling(_snapshots.Count / (double)MaxChartPoints));
                        EquitySeries =
                        [
                            new ChartSeries("Buy & hold", Colors["Buy & hold"], reference),
                            new ChartSeries("Fly brain", Colors["Fly brain"], brainValues.Where((_, i) => i % stride == 0).ToArray()),
                        ];
                    }

                    if (ReplayDelayMilliseconds > 0)
                    {
                        await Task.Delay(ReplayDelayMilliseconds, token);
                    }
                },
                cancellationToken: cancellation.Token);
            Results.Clear();
            Results.Add(ToRow(result));
            Results.Add(ToRow(buyAndHold));
            StatusText = result.Completed ? "Replay complete." : "Replay stopped.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Replay stopped.";
        }
        catch (Exception exception)
        {
            StatusText = $"Replay failed: {exception.Message}";
            AddLog(StatusText);
        }
        finally
        {
            EndRun();
        }
    }

    public Task StopAsync()
    {
        _cancellation?.Cancel();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _cancellation?.Cancel();
        return ValueTask.CompletedTask;
    }

    internal static double[] Downsample(IReadOnlyList<double> values)
    {
        if (values.Count <= MaxChartPoints)
        {
            return values.ToArray();
        }

        var stride = (int)Math.Ceiling(values.Count / (double)MaxChartPoints);
        var result = new List<double>(MaxChartPoints + 1);
        for (var i = 0; i < values.Count; i += stride)
        {
            result.Add(values[i]);
        }

        if ((values.Count - 1) % stride != 0)
        {
            result.Add(values[^1]);
        }

        return result.ToArray();
    }

    private BrainTradingStrategy CreateBrainStrategy()
    {
        var strategy = new BrainTradingStrategy(_brain);
        strategy.Evaluated += (_, evaluation) =>
        {
            BuyScore = evaluation.Response.BuyScore;
            SellScore = evaluation.Response.SellScore;
            if (!evaluation.FromCache)
            {
                CacheSize = _brain.CacheSize;
                AddLog(string.Create(CultureInfo.InvariantCulture,
                    $"Brain: {MarketTokenText.Sequence(evaluation.Tokens)} -> {evaluation.Action.ToString().ToUpperInvariant()} (buy {evaluation.Response.BuyScore:F4}, sell {evaluation.Response.SellScore:F4}, {evaluation.Elapsed.TotalMilliseconds:F0} ms)"));
            }
        };
        return strategy;
    }

    private Task ReportBacktestStepAsync(BacktestStep step, CancellationToken cancellationToken)
    {
        if (step.Step % 250 == 0 || step.Step == step.TotalSteps - 1)
        {
            UpdateCurrent(step);
        }

        return Task.CompletedTask;
    }

    private void UpdateCurrent(BacktestStep step)
    {
        Progress = 100.0 * (step.Step + 1) / step.TotalSteps;
        CurrentTime = step.Snapshot.Candle.OpenTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
        CurrentPrice = (double)step.Snapshot.Candle.Close;
        CurrentTokens = MarketTokenText.Sequence(step.Snapshot.Tokens);
        CurrentDecision = step.Decision.Action.ToString().ToUpperInvariant() + (step.HasPosition ? " · holding BTC" : " · in cash");
        BrainEquity = (double)step.Equity;
    }

    private CancellationTokenSource BeginRun(string status)
    {
        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();
        IsRunning = true;
        Progress = 0;
        StatusText = status;
        return _cancellation;
    }

    private void EndRun()
    {
        IsRunning = false;
        _cancellation = null;
        CacheSize = _brain.CacheSize;
    }

    private void RefreshCommands()
    {
        LoadDataCommand.RaiseCanExecuteChanged();
        RunBacktestCommand.RaiseCanExecuteChanged();
        ReplayCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
    }

    private void AddLog(string line)
    {
        Log.Insert(0, $"{DateTime.Now:HH:mm:ss} {line}");
        while (Log.Count > MaxLogLines)
        {
            Log.RemoveAt(Log.Count - 1);
        }
    }

    private static StrategyResultRow ToRow(BacktestResult result) => new(
        result.StrategyName + (result.Completed ? string.Empty : " (stopped)"),
        result.FinalEquity.ToString("N2", CultureInfo.InvariantCulture),
        result.ReturnPercent.ToString("+0.00;-0.00", CultureInfo.InvariantCulture) + " %",
        result.MaxDrawdownPercent.ToString("0.00", CultureInfo.InvariantCulture) + " %",
        result.TradeCount,
        result.FeesPaid.ToString("N2", CultureInfo.InvariantCulture),
        $"{result.BuySignals}/{result.SellSignals}/{result.HoldSignals}");

    private static ChartSeries ToSeries(BacktestResult result) => new(
        result.StrategyName,
        Colors.TryGetValue(result.StrategyName, out var color) ? color : "#B48EFF",
        Downsample(result.Equity.Select(p => (double)p.Equity).ToArray()));
}

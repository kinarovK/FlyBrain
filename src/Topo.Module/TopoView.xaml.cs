using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Topo.Module.ViewModels;
using Topo.Core;

namespace Topo.Module;

public partial class TopoView : System.Windows.Controls.UserControl
{
    private readonly MainViewModel _viewModel;

    public TopoView(MainViewModel viewModel)
    {
        _viewModel = viewModel; InitializeComponent(); DataContext = viewModel;
        Loaded += async (_, _) => await InitializeMapAsync();
        _viewModel.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName is nameof(MainViewModel.Target) or nameof(MainViewModel.Decoded) or nameof(MainViewModel.CurrentDecoded) or nameof(MainViewModel.BestDecoded) or nameof(MainViewModel.SearchTrail) or nameof(MainViewModel.ApplicationMode)) UpdateMarkers();
        };
    }

    private bool _mapInitialized;

    private async Task InitializeMapAsync()
    {
        if (_mapInitialized) return;
        _mapInitialized = true;
        try
        {
            await MapView.EnsureCoreWebView2Async();
            var folder = Path.Combine(AppContext.BaseDirectory, "Assets", "Map");
            MapView.CoreWebView2.SetVirtualHostNameToFolderMapping("topo.local", folder, CoreWebView2HostResourceAccessKind.Allow);
            MapView.CoreWebView2.WebMessageReceived += (_, args) => HandleMapMessage(args.WebMessageAsJson);
            MapView.NavigationCompleted += (_, _) => UpdateMarkers();
            MapView.Source = new Uri("https://topo.local/index.html");
        }
        catch { MapFallback.Visibility = Visibility.Visible; }
    }

    private void HandleMapMessage(string json)
    {
        try
        {
            using var message = JsonDocument.Parse(json);
            if (message.RootElement.GetProperty("type").GetString() == "map-click")
                _viewModel.SelectMapCoordinate(message.RootElement.GetProperty("lat").GetDouble(), message.RootElement.GetProperty("lon").GetDouble());
        }
        catch { /* Browser messages never control neural execution directly. */ }
    }

    private void UpdateMarkers()
    {
        if (MapView.CoreWebView2 is null) return;
        if (GeoCoordinate.TryParse(_viewModel.Target, out var target)) Execute($"topoMap.setTarget({target.LatitudeDegrees},{target.LongitudeDegrees},{JsonSerializer.Serialize(target.ToString())})");
        if (_viewModel.ApplicationMode == ApplicationMode.AutonomousSearch)
        {
            if (GeoCoordinate.TryParse(_viewModel.CurrentDecoded, out var current)) Execute($"topoMap.setCurrent({current.LatitudeDegrees},{current.LongitudeDegrees},{JsonSerializer.Serialize($"Decoded search guess: {current}")})");
            if (GeoCoordinate.TryParse(_viewModel.BestDecoded, out var best)) Execute($"topoMap.setBest({best.LatitudeDegrees},{best.LongitudeDegrees},{JsonSerializer.Serialize($"Best decoded guess: {best}")})");
            var trail = _viewModel.SearchTrail.Select(point => new[] { point.LatitudeDegrees, point.LongitudeDegrees });
            Execute($"topoMap.setTrail({JsonSerializer.Serialize(trail)})");
        }
        else
        {
            Execute("topoMap.clearSearch()");
            if (GeoCoordinate.TryParse(_viewModel.Decoded, out var decoded)) Execute($"topoMap.setFly({decoded.LatitudeDegrees},{decoded.LongitudeDegrees},{JsonSerializer.Serialize($"Decoded neural prediction: {decoded}")})");
        }
    }

    private void Execute(string script) => _ = MapView.CoreWebView2!.ExecuteScriptAsync($"window.topoMap&&{script}");
}

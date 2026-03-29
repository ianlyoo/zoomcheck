using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ZoomCheck.App.ViewModels;

namespace ZoomCheck.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Opened += async (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                await vm.InitializeCommand.ExecuteAsync(null);
            }
        };
    }

    private async void BrowseRosterFile_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose roster Excel file",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Excel files")
                {
                    Patterns = new[] { "*.xlsx", "*.xls" }
                }
            }
        });

        var file = files.FirstOrDefault();
        if (file is null)
        {
            return;
        }

        if (DataContext is MainWindowViewModel vm)
        {
            vm.RosterFilePath = file.Path.LocalPath;
        }
    }

    private async void Export_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var suggested = $"{vm.MeetingId}-attendance.csv";
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save attendance export",
            SuggestedFileName = suggested,
            FileTypeChoices = new[]
            {
                new FilePickerFileType("CSV")
                {
                    Patterns = new[] { "*.csv" }
                }
            }
        });

        if (file is null)
        {
            return;
        }

        var csv = await vm.GetExportCsvAsync();
        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(csv);
    }
}

using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using DiscForge.Core;
using Microsoft.Win32;

namespace DiscForge.App;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly LoadedDisc _disc;
    private CancellationTokenSource? _cancellation;
    private string? _parentFolder;
    private Screen _screen;

    private string _conflictPath = string.Empty;
    private string _phaseTitle = "Copie";
    private string _currentFile = string.Empty;
    private double _progressValue;
    private string _progressDetail = string.Empty;
    private string _resultTitle = string.Empty;
    private string _resultDetail = string.Empty;
    private string _issueText = string.Empty;
    private string _destinationPath = string.Empty;
    private bool _instructionsOpen;

    public MainViewModel(LoadedDisc disc)
    {
        _disc = disc;
        _screen = disc.CanCopy ? Screen.Ready : Screen.Error;

        var presentation = disc.Presentation;
        Title = presentation?.Title ?? "PLAY";
        Subtitle = presentation?.Subtitle ?? string.Empty;
        Edition = presentation?.Edition ?? string.Empty;
        FilesystemLine = presentation?.Filesystem is { Length: > 0 } filesystem
            ? $"Système de fichiers : {filesystem}"
            : string.Empty;
        Instructions = presentation?.Instructions ?? GameText.DefaultInstructions;
        ArtworkPath = presentation?.ArtworkPath;
        FolderHint = presentation == null
            ? string.Empty
            : $"Un dossier « {presentation.FolderName} » sera créé dans l'emplacement choisi.";

        if (disc.Inventory != null)
        {
            StatsLine = $"{ByteText.FileCount(disc.Inventory.Files.Count)}  ·  {ByteText.Format(disc.Inventory.TotalBytes)}";
        }

        NoticeText = string.Join(Environment.NewLine + Environment.NewLine, disc.Notices.Select(notice => notice.Message));
        BlockingError = disc.BlockingError ?? string.Empty;

        CopyCommand = new RelayCommand(CopyAsync, () => _screen == Screen.Ready && _disc.CanCopy);
        CancelCommand = new RelayCommand(Cancel, () => _screen == Screen.Working);
        OpenFolderCommand = new RelayCommand(OpenFolder, () => CanOpenFolder);
        ToggleInstructionsCommand = new RelayCommand(() => InstructionsOpen = !InstructionsOpen);
        ReplaceCommand = new RelayCommand(() => ContinueAfterConflict(ExistingFilePolicy.Replace));
        KeepCommand = new RelayCommand(() => ContinueAfterConflict(ExistingFilePolicy.Keep));
        ElsewhereCommand = new RelayCommand(CopyAsync);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string WindowTitle => Title == "PLAY" ? "PLAY" : $"{Title} — PLAY";

    public string Title { get; }

    public string Subtitle { get; }

    public string Edition { get; }

    public string FilesystemLine { get; }

    public string Instructions { get; }

    public string? ArtworkPath { get; }

    public string FolderHint { get; } = string.Empty;

    public string StatsLine { get; } = string.Empty;

    public string NoticeText { get; }

    public string BlockingError { get; }

    public bool HasNotices => NoticeText.Length > 0;

    public RelayCommand CopyCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand OpenFolderCommand { get; }

    public RelayCommand ToggleInstructionsCommand { get; }

    public RelayCommand ReplaceCommand { get; }

    public RelayCommand KeepCommand { get; }

    public RelayCommand ElsewhereCommand { get; }

    public bool ShowError => _screen == Screen.Error;

    public bool ShowReady => _screen == Screen.Ready;

    public bool ShowConflict => _screen == Screen.Conflict;

    public bool ShowWorking => _screen == Screen.Working;

    public bool ShowDone => _screen == Screen.Done;

    public bool ShowInstructionButton => _screen is Screen.Ready or Screen.Error;

    public bool ShowNotices => _screen == Screen.Ready && HasNotices;

    public bool ShowIssues => _screen == Screen.Done && HasIssues;

    public bool ShowInstructions => _instructionsOpen || _screen == Screen.Done;

    public bool CanOpenFolder => !string.IsNullOrWhiteSpace(_destinationPath) && Directory.Exists(_destinationPath);

    public string ConflictPath
    {
        get => _conflictPath;
        private set => Set(ref _conflictPath, value);
    }

    public string PhaseTitle
    {
        get => _phaseTitle;
        private set => Set(ref _phaseTitle, value);
    }

    public string CurrentFile
    {
        get => _currentFile;
        private set => Set(ref _currentFile, value);
    }

    public double ProgressValue
    {
        get => _progressValue;
        private set => Set(ref _progressValue, value);
    }

    public string ProgressDetail
    {
        get => _progressDetail;
        private set => Set(ref _progressDetail, value);
    }

    public string ResultTitle
    {
        get => _resultTitle;
        private set => Set(ref _resultTitle, value);
    }

    public string ResultDetail
    {
        get => _resultDetail;
        private set => Set(ref _resultDetail, value);
    }

    public string IssueText
    {
        get => _issueText;
        private set => Set(ref _issueText, value);
    }

    public bool HasIssues => IssueText.Length > 0;

    public string DestinationPath
    {
        get => _destinationPath;
        private set
        {
            if (Set(ref _destinationPath, value))
            {
                OnPropertyChanged(nameof(CanOpenFolder));
                OpenFolderCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool InstructionsOpen
    {
        get => _instructionsOpen;
        private set
        {
            if (Set(ref _instructionsOpen, value))
                OnPropertyChanged(nameof(ShowInstructions));
        }
    }

    public void Cancel() => _cancellation?.Cancel();

    private async Task CopyAsync()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choisir l'emplacement de la copie",
            Multiselect = false,
        };
        if (!string.IsNullOrWhiteSpace(_parentFolder))
            dialog.InitialDirectory = _parentFolder;

        var owner = Application.Current?.MainWindow;
        var accepted = owner == null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        if (accepted != true || string.IsNullOrWhiteSpace(dialog.FolderName))
            return;

        _parentFolder = dialog.FolderName;
        if (!TryDestination(out var destination, out var error))
        {
            ShowFinished(
                "Échec de la copie ou de la vérification.",
                error ?? "Le dossier de destination est invalide.",
                string.Empty,
                string.Empty);
            return;
        }

        if (Directory.Exists(destination) && FileTransfer.HasContent(destination))
        {
            ConflictPath = destination;
            SetScreen(Screen.Conflict);
            return;
        }

        await RunAsync(destination, ExistingFilePolicy.Replace).ConfigureAwait(true);
    }

    private async Task ContinueAfterConflict(ExistingFilePolicy policy)
    {
        if (!TryDestination(out var destination, out var error))
        {
            ShowFinished(
                "Échec de la copie ou de la vérification.",
                error ?? "Le dossier de destination est invalide.",
                string.Empty,
                string.Empty);
            return;
        }

        await RunAsync(destination, policy).ConfigureAwait(true);
    }

    private bool TryDestination(out string destination, out string? error)
    {
        destination = string.Empty;
        error = null;
        var folderName = _disc.Presentation?.FolderName ?? GameText.FallbackTitle;
        if (string.IsNullOrWhiteSpace(_parentFolder)
            || !PathGuard.TryResolveChild(_parentFolder, folderName, out destination, out error))
        {
            error ??= "Le dossier de destination est invalide.";
            return false;
        }

        return true;
    }

    private async Task RunAsync(string destination, ExistingFilePolicy policy)
    {
        SetScreen(Screen.Working);
        PhaseTitle = "Copie";
        CurrentFile = "Préparation…";
        ProgressValue = 0;
        ProgressDetail = string.Empty;
        _cancellation = new CancellationTokenSource();
        var progress = new Progress<TransferProgress>(ApplyProgress);

        try
        {
            var result = await FileTransfer.RunAsync(
                new TransferRequest
                {
                    GameFilesDirectory = _disc.GameFilesDirectory!,
                    DestinationDirectory = destination,
                    DiscRoot = _disc.DiscRoot,
                    Inventory = _disc.Inventory!,
                    Policy = policy,
                    Verification = _disc.Verification,
                    DiscNotices = _disc.Notices,
                },
                progress,
                _cancellation.Token).ConfigureAwait(true);

            ShowFinished(
                TransferWording.Summarize(result),
                Describe(result),
                FormatIssues(result),
                result.DestinationDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowFinished(
                "Échec de la copie ou de la vérification.",
                exception.Message,
                string.Empty,
                destination);
        }
    }

    private void ApplyProgress(TransferProgress progress)
    {
        PhaseTitle = progress.Phase == TransferPhase.Verifying ? "Vérification" : "Copie";
        CurrentFile = string.IsNullOrWhiteSpace(progress.CurrentRelativePath)
            ? "Préparation…"
            : progress.CurrentRelativePath;

        ProgressValue = progress.TotalBytes > 0
            ? Math.Clamp(progress.CompletedBytes * 100d / progress.TotalBytes, 0, 100)
            : progress.TotalFiles == 0
                ? 0
                : Math.Clamp(progress.CompletedFiles * 100d / progress.TotalFiles, 0, 100);

        ProgressDetail = $"{progress.CompletedFiles} / {ByteText.FileCount(progress.TotalFiles)}   ·   {ByteText.Format(progress.CompletedBytes)} / {ByteText.Format(progress.TotalBytes)}";
    }

    private void ShowFinished(string title, string detail, string issues, string destination)
    {
        ResultTitle = title;
        ResultDetail = detail;
        IssueText = issues;
        DestinationPath = destination;
        InstructionsOpen = true;
        OnPropertyChanged(nameof(HasIssues));
        SetScreen(Screen.Done);
        OnPropertyChanged(nameof(ShowIssues));
    }

    private void OpenFolder()
    {
        if (!CanOpenFolder)
            return;

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{DestinationPath}\"",
            UseShellExecute = true,
        });
    }

    private void SetScreen(Screen screen)
    {
        _screen = screen;
        OnPropertyChanged(nameof(ShowError));
        OnPropertyChanged(nameof(ShowReady));
        OnPropertyChanged(nameof(ShowConflict));
        OnPropertyChanged(nameof(ShowWorking));
        OnPropertyChanged(nameof(ShowDone));
        OnPropertyChanged(nameof(ShowInstructionButton));
        OnPropertyChanged(nameof(ShowInstructions));
        OnPropertyChanged(nameof(ShowNotices));
        OnPropertyChanged(nameof(ShowIssues));
        CopyCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
    }

    private static string Describe(TransferResult result)
    {
        return result.Outcome switch
        {
            TransferOutcome.Verified => "Les empreintes SHA-256 correspondent au manifeste.",
            TransferOutcome.SizeChecked => "Aucun manifeste SHA-256 n'a été utilisé.",
            TransferOutcome.Warnings when result.HashesChecked => "Les empreintes SHA-256 disponibles ont été calculées.",
            TransferOutcome.Warnings => "Aucune empreinte n'a été vérifiée.",
            TransferOutcome.Failed when result.HashesChecked => "La vérification SHA-256 a échoué.",
            TransferOutcome.Cancelled => "La copie déjà effectuée est conservée.",
            _ => "La copie n'a pas abouti.",
        };
    }

    private static string FormatIssues(TransferResult result)
    {
        var lines = result.DiscNotices
            .Select(notice => notice.Message)
            .Concat(result.Issues.Select(issue => issue.RelativePath == null
                ? issue.Detail
                : $"{issue.RelativePath} — {issue.Detail}"));

        return string.Join(Environment.NewLine + Environment.NewLine, lines);
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private enum Screen
    {
        Error,
        Ready,
        Conflict,
        Working,
        Done,
    }
}

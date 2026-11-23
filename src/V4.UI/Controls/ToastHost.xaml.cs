#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using BiosOptimizer.Core.Services;

namespace V4.UI.Controls
{
    public sealed class ToastItem : INotifyPropertyChanged
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");

        private string _title = string.Empty;
        public string Title
        {
            get => _title;
            set { _title = value; OnPropertyChanged(); }
        }

        private string? _subtitle;
        public string? Subtitle
        {
            get => _subtitle;
            set { _subtitle = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasSubtitle)); }
        }

        public bool HasSubtitle => !string.IsNullOrEmpty(Subtitle);

        private ToastKind _kind = ToastKind.Success;
        public ToastKind Kind
        {
            get => _kind;
            set
            {
                _kind = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(AccentBrush));
                OnPropertyChanged(nameof(IconText));
            }
        }

        private double _opacity = 1.0;
        public double Opacity
        {
            get => _opacity;
            set { _opacity = value; OnPropertyChanged(); }
        }

        public TimeSpan Duration { get; set; } = TimeSpan.FromSeconds(5);
        public DispatcherTimer? Timer { get; set; }
        public DateTimeOffset CreatedUtc { get; } = DateTimeOffset.UtcNow;

        public Brush AccentBrush => Kind switch
        {
            ToastKind.Success => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981")),
            ToastKind.Warning => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F59E0B")),
            ToastKind.Error => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444")),
            ToastKind.Info => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3B82F6")),
            _ => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981"))
        };

        public string IconText => Kind switch
        {
            ToastKind.Success => "✓",
            ToastKind.Warning => "⚠",
            ToastKind.Error => "✕",
            ToastKind.Info => "ℹ",
            _ => "✓"
        };

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public sealed partial class ToastHost : UserControl, IToastHost
    {
        public ObservableCollection<ToastItem> Toasts { get; } = new();

        public int ActiveToastCount => Toasts.Count;
        public IReadOnlyList<ToastItem> ActiveToasts => Toasts.ToList();

        public ToastHost()
        {
            InitializeComponent();
        }

        public void Show(string title, string? subtitle = null,
                         ToastKind kind = ToastKind.Success,
                         TimeSpan? duration = null)
        {
            if (Dispatcher != null && !Dispatcher.CheckAccess())
            {
                try
                {
                    Dispatcher.Invoke(() => Show(title, subtitle, kind, duration));
                }
                catch
                {
                    Dispatcher.BeginInvoke(() => Show(title, subtitle, kind, duration));
                }
                return;
            }

            TimeSpan finalDuration = duration ?? TimeSpan.FromSeconds(5);

            // Check if this is an update to an existing active backup toast
            if (title.Contains("backup", StringComparison.OrdinalIgnoreCase))
            {
                var existingBackupToast = Toasts.FirstOrDefault(t => t.Title.Contains("backup", StringComparison.OrdinalIgnoreCase));
                if (existingBackupToast != null)
                {
                    existingBackupToast.Title = title;
                    existingBackupToast.Subtitle = subtitle;
                    existingBackupToast.Kind = kind;
                    existingBackupToast.Duration = finalDuration;

                    // Reset its timer
                    if (existingBackupToast.Timer != null)
                    {
                        existingBackupToast.Timer.Stop();
                        existingBackupToast.Timer.Interval = finalDuration;
                        existingBackupToast.Timer.Start();
                    }
                    return;
                }
            }

            // Enforce max 3 toasts: evict oldest
            while (Toasts.Count >= 3)
            {
                var oldest = Toasts[Toasts.Count - 1];
                oldest.Timer?.Stop();
                Toasts.RemoveAt(Toasts.Count - 1);
            }

            var item = new ToastItem
            {
                Title = title,
                Subtitle = subtitle,
                Kind = kind,
                Duration = finalDuration,
                Opacity = 1.0
            };

            // Setup auto-dismiss timer
            var timer = new DispatcherTimer
            {
                Interval = finalDuration
            };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                Dismiss(item);
            };
            item.Timer = timer;
            timer.Start();

            // Insert at top (newest on top)
            Toasts.Insert(0, item);
        }

        public void Dismiss(ToastItem item)
        {
            if (Dispatcher != null && !Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished && !Dispatcher.CheckAccess())
            {
                try
                {
                    Dispatcher.Invoke(() => Dismiss(item));
                }
                catch { }
                return;
            }

            item.Timer?.Stop();
            Toasts.Remove(item);
        }

        public void Clear()
        {
            if (Dispatcher != null && !Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished && !Dispatcher.CheckAccess())
            {
                try
                {
                    Dispatcher.Invoke(Clear);
                }
                catch { }
                return;
            }

            foreach (var item in Toasts)
            {
                item.Timer?.Stop();
            }
            Toasts.Clear();
        }
    }
}

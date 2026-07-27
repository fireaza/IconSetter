using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Windows.Media.Imaging;
using IconSetter.Services;

namespace IconSetter.Models
{
    public class FolderResult : INotifyPropertyChanged
    {
        private string? selectedIcon;
        private string status = "Pending";
        private BitmapSource? previewImage;

        public string FolderPath { get; set; } = "";
        public List<string> IconFiles { get; set; } = new();

        /// <summary>True if this folder has at least one subfolder - shown as a small badge so the
        /// user knows it can be opened when browsing in Explorer-style mode.</summary>
        public bool HasSubfolders { get; set; }

        /// <summary>True if this folder currently has an unconverted source image (icon*.png/jpg/
        /// jpeg/bmp) sitting in it - independent of IconFiles, which only tracks .ico files. Drives
        /// the "With .ico / Needs conversion / Has both / No icon yet" filter chips.</summary>
        public bool HasUnconvertedImage { get; set; }

        private string iconState = "New";
        /// <summary>"New" (never customized), "Up to date" (already set to exactly this icon,
        /// unchanged since), or "Changed" (either pointing at a different icon than currently
        /// selected, or the icon file itself was edited since it was set).</summary>
        public string IconState
        {
            get => iconState;
            set
            {
                if (iconState != value)
                {
                    iconState = value;
                    OnPropertyChanged(nameof(IconState));
                    OnPropertyChanged(nameof(IconStateBrush));
                }
            }
        }

        /// <summary>Color-codes the IconState label: grey for "New", green for "Up to date",
        /// amber for "Changed".</summary>
        public System.Windows.Media.Brush IconStateBrush => IconState switch
        {
            "Up to date" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x9E, 0x5A)),
            "Changed" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xDB, 0x9A, 0x2C)),
            _ => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x6B, 0x6E, 0x7E))
        };

        /// <summary>Only folders that actually have an icon file worth talking about show a state
        /// label - plain navigation-only folders (Explorer-mode placeholders) don't.</summary>
        public bool ShowIconStateLabel => IconFiles.Count > 0;

        public string? SelectedIcon
        {
            get => selectedIcon;
            set
            {
                if (selectedIcon != value)
                {
                    selectedIcon = value;
                    OnPropertyChanged(nameof(SelectedIcon));
                    OnPropertyChanged(nameof(CurrentIconIndex));
                    OnPropertyChanged(nameof(IconPositionText));

                    try
                    {
                        PreviewImage = selectedIcon != null ? IconConverter.LoadLargestIconFrame(selectedIcon) : null;
                    }
                    catch
                    {
                        PreviewImage = null;
                    }
                }
            }
        }

        public string Status
        {
            get => status;
            set
            {
                if (status != value)
                {
                    status = value;
                    OnPropertyChanged(nameof(Status));
                }
            }
        }

        public BitmapSource? PreviewImage
        {
            get => previewImage;
            set
            {
                if (previewImage != value)
                {
                    previewImage = value;
                    OnPropertyChanged(nameof(PreviewImage));
                    OnPropertyChanged(nameof(HasNoPreview));
                }
            }
        }

        /// <summary>True when there's no icon image to show - the gallery falls back to a plain
        /// folder glyph so folders can still be browsed/navigated in Explorer-style mode.</summary>
        public bool HasNoPreview => PreviewImage == null;

        public int IconCount => IconFiles?.Count ?? 0;
        public int CurrentIconIndex => IconFiles != null && SelectedIcon != null ? IconFiles.IndexOf(SelectedIcon) + 1 : 0;

        public string IconPositionText =>
            HasMultipleIcons ? $"{CurrentIconIndex}/{IconCount}" : string.Empty;

        public bool HasMultipleIcons => IconFiles != null && IconFiles.Count > 1;

        public string? CustomName { get; set; }
        public string DisplayName =>
            !string.IsNullOrEmpty(CustomName) ? CustomName! : Path.GetFileName(FolderPath.TrimEnd(Path.DirectorySeparatorChar));

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

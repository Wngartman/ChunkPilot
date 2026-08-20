// WinForms is referenced only for the tray icon, but its types collide with WPF's on almost every
// common name. These aliases pin the WPF meaning for the whole project so no file has to remember to
// disambiguate; the tray-icon code reaches for WinForms explicitly through the Forms alias instead.
global using Application = System.Windows.Application;
global using Brush = System.Windows.Media.Brush;
global using Brushes = System.Windows.Media.Brushes;
global using Binding = System.Windows.Data.Binding;
global using Color = System.Windows.Media.Color;
global using Control = System.Windows.Controls.Control;
global using FontFamily = System.Windows.Media.FontFamily;
global using MessageBox = System.Windows.MessageBox;
global using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
global using Pen = System.Windows.Media.Pen;
global using Point = System.Windows.Point;
global using Size = System.Windows.Size;
global using UserControl = System.Windows.Controls.UserControl;
global using Window = System.Windows.Window;
global using System.IO;
global using System.Windows;
global using System.Windows.Controls;

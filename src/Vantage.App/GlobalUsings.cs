// The WinForms/WPF SDK replaces the default implicit usings and drops System.IO, so the
// namespaces this project relies on are declared here explicitly. The tray icon is the only
// WinForms surface in the product, so Application always means the WPF one.
global using System.IO;
global using Application = System.Windows.Application;

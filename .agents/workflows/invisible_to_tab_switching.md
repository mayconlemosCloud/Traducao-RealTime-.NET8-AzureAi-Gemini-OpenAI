---
description: Implement Invisibility to Tab Switching (Alt+Tab)
---

1. Open `MainWindow.xaml.cs`.
2. Locate the `MainWindow` constructor or `OnSourceInitialized`.
3. Add the following P/Invoke and constants:
   ```csharp
   [DllImport("user32.dll")]
   public static extern int GetWindowLong(IntPtr hwnd, int index);
   [DllImport("user32.dll")]
   public static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);
   const int GWL_EXSTYLE = -20;
   const int WS_EX_TOOLWINDOW = 0x00000080;
   ```
4. Get the window handle.
5. Apply the `WS_EX_TOOLWINDOW` style using `SetWindowLong`.
6. Verify by pressing Alt+Tab while the application is running.

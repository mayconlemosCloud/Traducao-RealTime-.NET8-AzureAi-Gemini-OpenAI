---
description: Implement Window Invisibility on Screen Share
---

1. Open `MainWindow.xaml.cs`.
2. Import `System.Runtime.InteropServices`.
3. Locate the `MainWindow` constructor or `OnSourceInitialized` override.
4. Add the following P/Invoke declaration:
   ```csharp
   [DllImport("user32.dll")]
   public static extern uint SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
   const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
   ```
5. In the window initialization logic, get the window handle using `WindowInteropHelper`.
6. Call `SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE)`.
7. Verify with a screen recording tool.

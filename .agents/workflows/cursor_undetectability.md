---
description: Implement Cursor Undetectability
---

1. Open `MainWindow.xaml`.
2. Locate the root `Window` tag.
3. Add the attribute `Cursor="None"`.
4. (Optional) To make the window click-through:
    - Open `MainWindow.xaml.cs`.
    - Add `WS_EX_TRANSPARENT = 0x00000020`.
    - Apply it via `SetWindowLong`.
5. Verify that the cursor disappears when hovering over the window and (if transparent) that clicks pass through.

---
description: Implement Invisibility in Task Manager (Process Visibility)
---

1. Ensure `ShowInTaskbar="False"` is set in `MainWindow.xaml`.
2. Ensure `WS_EX_TOOLWINDOW` style is applied in `MainWindow.xaml.cs`.
3. These two settings combined effectively move the application to the "Background Processes" section in Task Manager and remove it from the "Apps" list.
4. Verify by opening Task Manager and looking for `MeetingTranslator` under the "Apps" vs "Background Processes" sections.

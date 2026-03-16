# Stealth Features Plan

This document outlines the implementation of "Stealth Mode" for the MeetingTranslator project. These features are designed to make the application discreet and difficult to detect during active use.

## 1. Invisible on Screen Share
- **Description**: The application window will be completely invisible to screen recording software (OBS, Zoom, Discord, Teams, etc.).
- **Implementation**: Uses the Windows API `SetWindowDisplayAffinity` with `WDA_EXCLUDEFROMCAPTURE`.
- **Status**: Planned

## 2. Invisible in Dock (Taskbar)
- **Description**: The application icon will not appear in the Windows Taskbar when running.
- **Implementation**: Set `ShowInTaskbar="False"` in the `MainWindow` XAML.
- **Status**: Planned

## 3. Invisible to Tab Switching (Alt+Tab)
- **Description**: The application will not appear in the Alt+Tab menu, preventing accidental discovery when switching windows.
- **Implementation**: Set `WindowStyle="ToolWindow"` or use `WS_EX_TOOLWINDOW` extended window style via Win32 API.
- **Status**: Planned

## 4. Invisible in Task Manager (Process Presence)
- **Description**: While the process will still exist, it will be categorized as a "Background Process" and will not appear in the "Apps" section of the Task Manager.
- **Implementation**: Achieved by combining `ShowInTaskbar="False"` and `ToolWindow` styles, ensuring it doesn't have a top-level application entry.
- **Status**: Planned

## 5. Cursor Undetectability
- **Description**: The mouse cursor will either be hidden when hovering over the window or the window will be "click-through" (transparent to mouse events).
- **Implementation**: 
    - **Hidden Cursor**: Set `Cursor="None"`.
    - **Click-Through**: Use `WS_EX_TRANSPARENT` extended style.
- **Note**: A hotkey or toggle is recommended to re-enable interaction if click-through is active.
- **Status**: Planned

# Local Deskband11Lib changes

Upstream: `airtaxi/Deskband11Lib` 1.3.9, commit
`095ca312c3821bb7d7dc33cf5cfeb5bc4c73e1e0` (MIT; see `LICENSE`).

This local fork adds `TaskbarContentPlacement.AccountArea`. On a centered
taskbar it uses the safe Widgets-to-Start gap. On a left-aligned taskbar it
shares the physical post-buttons/pre-notification gap with
`BeforeNotificationArea`: account slots allocate from the left edge, system
slots from the right edge, and all slots share one bounded width budget.

The UI Automation reader also treats all supported visible interactive taskbar
control types as collision geometry, rather than considering buttons alone.

# Observable action feedback

`ObservableActionState` is the shared interaction contract for launcher work that needs visible and accessible
acknowledgement. It keeps two concerns separate:

- **Command availability** answers whether the surrounding application permits an action.
- **Operation status** is `Idle`, `Working`, `CompletedChanged`, `CompletedUnchanged`, `Failed`, or `Unavailable`.

Working state does not make an otherwise available command unavailable. `TryBegin` rejects a second activation while
work is active, allowing a WPF button to retain keyboard focus instead of using disabled state as its progress signal.
Consumers must bind `StatusText` (or `AutomationAnnouncement`) to visible text in a polite UI Automation live region.
Accepted, changed, unchanged, failed, and unavailable messages must say what happened; color, an icon, or animation may
supplement but never replace that text.

`ObservableActionCommand` is appropriate when one activation maps directly to one asynchronous result. Operations with
confirmation or multiple phases may drive the same state explicitly with `TryBegin`, `Complete`, `Fail`, and `Cancel`.
Do not report percentages unless the underlying service exposes truthful progress.

The initial consumers are:

- **Refresh status**: reports whether the projected Home state changed or was already current.
- **Community mod release/deployment**: reports accepted release discovery, an available release, already-current state,
  deployment completion, cancellation, and failure.

Action targets remain at least 44 DIPs and retain the shared focus visual. These consumers use no motion, so Windows
reduced-motion preferences need no alternate rendering. A future animated consumer must make motion optional while
preserving the same text and UI Automation transitions.

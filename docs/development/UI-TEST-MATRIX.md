# UI test matrix

| Area | Automated validation | Synthetic smoke |
|---|---|---|
| Resource loading | WPF resource dictionary and token key tests | App startup with isolated data root |
| Navigation | semantic destination and command enablement tests | switch global/server destinations |
| Lifecycle | existing reconnect/close integration tests | packaged WM_CLOSE with isolated fake agents |
| Console | bounded collection, follow/unseen behavior tests | emit accelerated fake console output |
| Safety | existing backup/update/path/EULA tests | failed staging and recovery fixture |
| Responsive | layout-mode and visibility tests | resize wide/standard/compact where desktop harness permits |
| Accessibility | names, focus, reduced motion, high contrast resource tests | keyboard-only smoke |
| Packaging | framework-dependent, self-contained, portable publish | isolated packaged startup/close and temp cleanup |

Full-screen screenshot capture is not acceptance evidence unless the captured window is positively identified as ChunkPilot; compositor or desktop pixels must be reported as invalid.

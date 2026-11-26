# Interactive neural-coordinate sandbox

Topo Phase 8 runs in **Neural Decode Mode**. A coordinate entered in the text
box, or rounded from a map click, is the user-provided source/target. The app
stimulates the fixed reference connectome, reads the accepted 128-root
population, uses the frozen external token classifier, and shows the decoded
prediction. The fly marker is a decoded neural prediction only; it is not an
autonomous fly, path, policy, or search action.

The Leaflet map is hosted in WebView2 with OpenStreetMap attribution visible.
Map clicks round to the nearest integer Topo coordinate; for example, `48.213,
22.645` becomes `N48 E23`. If WebView2 or tiles are unavailable, the desktop
app remains usable through manual input and shows a map-unavailable state.

On startup the app loads `data/models/token-readout-v1.json` and validates it
against the existing channel and readout-population files. It never retrains.
It does not start Brain Service: offline startup is normal, and **Reconnect**
creates one reference/unsigned session only after readiness and compatibility
checks succeed. That session is reset for each decode and deleted on shutdown.

The app preserves amplitude 100, one stimulus step, 22 settle steps, and a
22-step readout observation. Token progress and telemetry come from actual
pipeline callbacks. The reward is application-level only:

```text
reward = exp(-angularErrorDegrees / 20)
```

It is bounded in `[0, 1]`; it is not measured biological dopamine. There is
currently no neural END token, so the known source sequence boundary supplies
the decode boundary for this interactive experiment.

Phase 9 adds an explicit **Autonomous Search Mode** alongside, not instead of,
Neural Decode Mode. The user supplies only a target. A deterministic external
hill-climbing policy proposes valid integer coordinates, the frozen neural
pipeline decodes every proposal after a session reset, and the application
calculates reward from target versus decoded proposal. The map's fly marker is
the current decoded search guess, the green marker is the best decoded guess,
and the line is a bounded history of decoded evaluations. These visuals do not
represent biological locomotion or autonomous fly navigation.

Search uses seed `20260924`, mutation radii `30, 15, 8, 4, 2, 1`, global
exploration after 12 rejected evaluations, a maximum of 250 evaluations, and
a 100-item UI history/trail. The policy has no target-coordinate or
target-token input. Search Stop cancels the active frozen evaluation without
creating a replacement session; Reset clears presentation state only.

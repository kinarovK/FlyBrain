# End-to-end coordinate decoding

Phase 7 loads the frozen Phase 6 token-readout artifact and reconstructs a
complete `GeoCoordinate` from neural observations. It does not train, refit,
or modify the token classifier.

The grammar is:

```text
LatitudeHemisphere LatitudeDigit+ Separator LongitudeHemisphere LongitudeDigit+
```

Only `North`/`South`, digit tokens, one separator, and `East`/`West` are valid
in their respective positions. Missing components, duplicate separators,
unexpected tokens, latitudes above 90, and longitudes above 180 return a
structured failure; no prediction is clamped or repaired. Decoding constructs
the existing `Topo.Core.GeoCoordinate`, so `South 0 ... West 0` naturally
normalizes to `N0 E0` under the established Phase 3 semantics.

The neural pipeline resets one reference/unsigned session per coordinate,
stimulates each source token for one step at amplitude 100, settles exactly 22
steps, reads the accepted 128 roots, and applies the frozen model. The
classifier receives only extracted neural features. The source coordinate and
expected tokens remain evaluation data outside that classifier path.

An external sequence boundary is currently supplied by the experiment runner;
there is no neural END token. This is a known limitation and must not be
mistaken for autonomous coordinate recognition.

The opt-in evaluation writes reports, representative traces, and any failures
under `data/experiments/coordinate-decoding-v1`. Its blind corpus uses seed
`20260923`, reconstructs the documented Phase 6 training set to exclude it,
and reports that provenance because the original Phase 6 artifact did not save
coordinate identities. It measures exact-coordinate accuracy, per-token and
component accuracy, invalid sequences, angular error, and confidence scores.

This is computational neural encoding and external readout over a fixed
connectome. It does not demonstrate biological geographic understanding.

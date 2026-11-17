# Market data (local, not in Git)

The Trader module replays Binance public spot klines offline. Nothing is
downloaded by the app, and no exchange account or API key is used.

1. Open <https://data.binance.vision/?prefix=data/spot/monthly/klines/BTCUSDT/1h/>.
2. Download the twelve 2024 monthly files `BTCUSDT-1h-2024-01.zip` …
   `BTCUSDT-1h-2024-12.zip`, and optionally their `.CHECKSUM` files.
3. Put them, still zipped, into `data/market/binance/`.

The loader verifies `.CHECKSUM` files (SHA-256) when present, merges the months
in time order and reports gaps. Both the millisecond (≤2024) and microsecond
(2025+) timestamp formats are accepted. Other years work too; the first 30 days
are used as warm-up for the volatility regime.

These files are excluded by `.gitignore`. Check Binance's terms before
redistributing the data.

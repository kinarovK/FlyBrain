from __future__ import annotations

import uvicorn

from brain_service.api import create_app
from brain_service.config import load_service_config


def main() -> None:
    config = load_service_config()
    uvicorn.run(create_app(config=config), host=config.host, port=config.port, log_level=config.log_level.lower())


if __name__ == "__main__":
    main()

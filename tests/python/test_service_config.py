from brain_service.config import load_service_config


def test_default_config_does_not_prepare_whole_domain_eagerly(monkeypatch):
    monkeypatch.delenv("FLYBRAIN_EAGER_WHOLE", raising=False)
    assert load_service_config().eager_whole_domain is False


def test_eager_whole_domain_can_be_enabled_from_environment(monkeypatch):
    monkeypatch.setenv("FLYBRAIN_EAGER_WHOLE", "1")
    assert load_service_config().eager_whole_domain is True
    monkeypatch.setenv("FLYBRAIN_EAGER_WHOLE", "off")
    assert load_service_config().eager_whole_domain is False

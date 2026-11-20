CREATE TABLE IF NOT EXISTS public.request_source_credentials (
    key text PRIMARY KEY CHECK (key IN ('hubcap_api_key', 'hubcap_api_key_1', 'hubcap_api_key_2', 'lua_tools_cookie')),
    value text NOT NULL DEFAULT '',
    updated_at timestamptz NOT NULL DEFAULT now(),
    updated_by_device_id text
);

ALTER TABLE public.request_source_credentials
    DROP CONSTRAINT IF EXISTS request_source_credentials_key_check;
ALTER TABLE public.request_source_credentials
    ADD CONSTRAINT request_source_credentials_key_check
    CHECK (key IN ('hubcap_api_key', 'hubcap_api_key_1', 'hubcap_api_key_2', 'lua_tools_cookie'));

ALTER TABLE public.request_source_credentials ENABLE ROW LEVEL SECURITY;

REVOKE ALL ON TABLE public.request_source_credentials FROM PUBLIC, anon, authenticated;
GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE public.request_source_credentials TO service_role;

INSERT INTO public.request_source_credentials AS stored (key, value)
SELECT 'hubcap_api_key', value
FROM public.app_remote_config
WHERE key = 'hubcap_api_key'
ON CONFLICT (key) DO UPDATE
SET value = CASE
    WHEN stored.value = '' THEN EXCLUDED.value
    ELSE stored.value
END;

INSERT INTO public.request_source_credentials AS stored (key, value)
SELECT 'hubcap_api_key_1', value
FROM public.request_source_credentials
WHERE key = 'hubcap_api_key'
ON CONFLICT (key) DO UPDATE
SET value = CASE
    WHEN stored.value = '' THEN EXCLUDED.value
    ELSE stored.value
END;

DELETE FROM public.app_remote_config
WHERE key = 'hubcap_api_key';

INSERT INTO public.request_source_credentials (key, value)
VALUES
    ('hubcap_api_key', ''),
    ('hubcap_api_key_1', ''),
    ('hubcap_api_key_2', ''),
    ('lua_tools_cookie', '')
ON CONFLICT (key) DO NOTHING;

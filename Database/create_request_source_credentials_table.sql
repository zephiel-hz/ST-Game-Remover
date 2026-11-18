CREATE TABLE IF NOT EXISTS public.request_source_credentials (
    key text PRIMARY KEY CHECK (key IN ('hubcap_api_key', 'lua_tools_cookie')),
    value text NOT NULL DEFAULT '',
    updated_at timestamptz NOT NULL DEFAULT now(),
    updated_by_device_id text
);

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

DELETE FROM public.app_remote_config
WHERE key = 'hubcap_api_key';

INSERT INTO public.request_source_credentials (key, value)
VALUES
    ('hubcap_api_key', ''),
    ('lua_tools_cookie', '')
ON CONFLICT (key) DO NOTHING;

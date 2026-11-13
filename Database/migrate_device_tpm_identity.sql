-- Run this migration before releasing the client update.
-- It maps a TPM endorsement-key hash to the device_id already used by the app,
-- preserving existing token bindings, profiles, messages, and ban records.

ALTER TABLE public.device_tokens
    ADD COLUMN IF NOT EXISTS is_active BOOLEAN DEFAULT TRUE;

CREATE TABLE IF NOT EXISTS public.device_tpm_aliases (
    tpm_hash TEXT PRIMARY KEY
        CHECK (tpm_hash ~ '^[A-Fa-f0-9]{64}$'),
    device_id TEXT NOT NULL UNIQUE
        CHECK (length(trim(device_id)) BETWEEN 4 AND 256),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

ALTER TABLE public.device_tpm_aliases ENABLE ROW LEVEL SECURITY;
REVOKE ALL ON TABLE public.device_tpm_aliases FROM PUBLIC, anon, authenticated;
GRANT SELECT, INSERT ON TABLE public.device_tpm_aliases TO service_role;

CREATE OR REPLACE FUNCTION public.link_device_tpm_identity(
    p_tpm_hash TEXT,
    p_device_id TEXT
)
RETURNS TEXT
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public, pg_temp
AS $$
DECLARE
    existing_device_id TEXT;
BEGIN
    IF p_tpm_hash IS NULL OR p_tpm_hash !~ '^[A-Fa-f0-9]{64}$'
       OR p_device_id IS NULL OR length(trim(p_device_id)) NOT BETWEEN 4 AND 256 THEN
        RAISE EXCEPTION 'Invalid device identity';
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM public.device_tokens token
        WHERE token.is_active IS TRUE
          AND (
              token.device_id = p_device_id
              OR p_device_id = ANY(COALESCE(token.device_ids, ARRAY[]::TEXT[]))
          )
    ) AND NOT EXISTS (
        SELECT 1 FROM public.blocked_devices blocked
        WHERE blocked.device_id = p_device_id
    ) THEN
        RETURN NULL;
    END IF;

    INSERT INTO public.device_tpm_aliases (tpm_hash, device_id)
    VALUES (lower(p_tpm_hash), trim(p_device_id))
    ON CONFLICT (tpm_hash) DO NOTHING;

    SELECT alias.device_id
    INTO existing_device_id
    FROM public.device_tpm_aliases alias
    WHERE alias.tpm_hash = lower(p_tpm_hash);

    IF existing_device_id IS DISTINCT FROM trim(p_device_id) THEN
        RAISE EXCEPTION 'TPM identity is already linked to a different device';
    END IF;

    RETURN existing_device_id;
END;
$$;

REVOKE ALL ON FUNCTION public.link_device_tpm_identity(TEXT, TEXT)
    FROM PUBLIC, anon, authenticated;
GRANT EXECUTE ON FUNCTION public.link_device_tpm_identity(TEXT, TEXT)
    TO service_role;

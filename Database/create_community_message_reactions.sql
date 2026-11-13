-- Run this migration in the Supabase SQL Editor.

CREATE TABLE IF NOT EXISTS public.community_message_reactions (
    message_id BIGINT NOT NULL
        REFERENCES public.community_messages(id) ON DELETE CASCADE,
    device_id TEXT NOT NULL CHECK (length(trim(device_id)) > 0),
    emoji TEXT NOT NULL CHECK (length(emoji) BETWEEN 1 AND 32),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (message_id, device_id, emoji)
);

CREATE INDEX IF NOT EXISTS idx_community_message_reactions_message_id
    ON public.community_message_reactions(message_id);

DELETE FROM public.community_message_reactions older
USING public.community_message_reactions newer
WHERE older.message_id = newer.message_id
  AND older.device_id = newer.device_id
  AND (older.created_at, older.emoji) < (newer.created_at, newer.emoji);

CREATE UNIQUE INDEX IF NOT EXISTS idx_community_message_reactions_one_per_device
    ON public.community_message_reactions(message_id, device_id);

ALTER TABLE public.community_message_reactions ENABLE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS "Allow public select on community_message_reactions"
    ON public.community_message_reactions;
CREATE POLICY "Allow public select on community_message_reactions"
    ON public.community_message_reactions
    FOR SELECT
    TO anon, authenticated
    USING (true);

DROP POLICY IF EXISTS "Allow public insert on community_message_reactions"
    ON public.community_message_reactions;
CREATE POLICY "Allow public insert on community_message_reactions"
    ON public.community_message_reactions
    FOR INSERT
    TO anon, authenticated
    WITH CHECK (
        length(trim(device_id)) > 0
        AND length(emoji) BETWEEN 1 AND 32
    );

DROP POLICY IF EXISTS "Allow public delete on community_message_reactions"
    ON public.community_message_reactions;
CREATE POLICY "Allow public delete on community_message_reactions"
    ON public.community_message_reactions
    FOR DELETE
    TO anon, authenticated
    USING (length(trim(device_id)) > 0);

GRANT SELECT, INSERT, DELETE
    ON TABLE public.community_message_reactions
    TO anon, authenticated;

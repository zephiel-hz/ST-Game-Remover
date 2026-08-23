-- Create games_dlc table to store DLC entries parsed from lua files
CREATE TABLE IF NOT EXISTS games_dlc (
    id BIGSERIAL PRIMARY KEY,
    game_app_id INTEGER NOT NULL REFERENCES hzmanifest_files(app_id) ON DELETE CASCADE,
    dlc_app_id INTEGER NOT NULL,
    dlc_name TEXT,
    dlc_fetch_date TIMESTAMP WITH TIME ZONE DEFAULT NOW(),
    UNIQUE(game_app_id, dlc_app_id)
);

CREATE INDEX IF NOT EXISTS idx_games_dlc_game_app_id ON games_dlc(game_app_id);
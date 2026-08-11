# Service Configuration

Konfigurasi Supabase dan Cloudflare R2 sekarang dipusatkan di file:

- Config/service-config.json

## Cara mengubah konfigurasi

Edit file tersebut sesuai kebutuhan:

- Supabase: Url, StorageUrl, AnonKey, ServiceRoleKey, StorageBucketName
- Cloudflare R2: Endpoint, AccessKey, SecretKey, Bucket

## Catatan

- Jika mengubah file konfigurasi saat aplikasi sedang berjalan, panggil `ServiceConfiguration.Reload()` agar nilai terbaru terbaca.
- Jangan commit kredensial sensitif ke repo publik.

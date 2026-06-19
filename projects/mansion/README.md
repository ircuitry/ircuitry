# Tiny PHP image upload endpoint

## Run
Serve the directory with PHP's built-in server:

```bash
php -S localhost:8000
```

Then POST to:

- `http://localhost:8000/upload.php`

(If using Apache + `.htaccess`, you can also try `/upload`.)

## Upload
Send `multipart/form-data` with field name `file`.
Optional field `folder` sets a subfolder under `uploads/`.

```bash
curl -X POST http://localhost:8000/upload.php \
  -F 'file=@/path/to/photo.jpg' \
  -F 'folder=myalbum'
```

## Response
JSON:
- `ok: true` with `url` to the uploaded image
- otherwise `ok: false` with `error`

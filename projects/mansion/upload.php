<?php
declare(strict_types=1);

// Simple image upload endpoint (POST multipart/form-data)
// Fields:
// - file: uploaded image
// - folder (optional): subfolder name under uploads/

header('Content-Type: application/json; charset=utf-8');

if ($_SERVER['REQUEST_METHOD'] !== 'POST') {
  http_response_code(405);
  echo json_encode(['ok' => false, 'error' => 'Method not allowed']);
  exit;
}

$uploadDir = __DIR__ . '/uploads';
if (!is_dir($uploadDir)) {
  mkdir($uploadDir, 0775, true);
}

$folder = isset($_POST['folder']) ? (string)$_POST['folder'] : '';
// allow only safe folder names
if ($folder !== '' && !preg_match('~^[a-zA-Z0-9_-]{1,64}$~', $folder)) {
  http_response_code(400);
  echo json_encode(['ok' => false, 'error' => 'Invalid folder']);
  exit;
}

$targetDir = $uploadDir . ($folder !== '' ? '/' . $folder : '');
if (!is_dir($targetDir)) {
  mkdir($targetDir, 0775, true);
}

if (!isset($_FILES['file'])) {
  http_response_code(400);
  echo json_encode(['ok' => false, 'error' => 'Missing file field']);
  exit;
}

$f = $_FILES['file'];
if (!is_array($f) || ($f['error'] ?? UPLOAD_ERR_NO_FILE) !== UPLOAD_ERR_OK) {
  http_response_code(400);
  echo json_encode(['ok' => false, 'error' => 'Upload failed']);
  exit;
}

$originalName = (string)($f['name'] ?? 'image');
$tmp = (string)($f['tmp_name'] ?? '');
$size = (int)($f['size'] ?? 0);

if ($size <= 0) {
  http_response_code(400);
  echo json_encode(['ok' => false, 'error' => 'Empty file']);
  exit;
}

// Validate MIME using finfo
$finfo = new finfo(FILEINFO_MIME_TYPE);
$mime = $finfo->file($tmp);

$allowed = [
  'image/jpeg' => 'jpg',
  'image/png' => 'png',
  'image/gif' => 'gif',
  'image/webp' => 'webp',
];

if (!isset($allowed[$mime])) {
  http_response_code(415);
  echo json_encode(['ok' => false, 'error' => 'Unsupported image type', 'mime' => $mime]);
  exit;
}

$ext = $allowed[$mime];
$base = bin2hex(random_bytes(16));
$clientHint = preg_replace('~[^a-zA-Z0-9_-]~', '-', pathinfo($originalName, PATHINFO_FILENAME));
$clientHint = $clientHint !== '' ? $clientHint : 'image';

$filename = $clientHint . '-' . $base . '.' . $ext;
$targetPath = $targetDir . '/' . $filename;

if (!move_uploaded_file($tmp, $targetPath)) {
  http_response_code(500);
  echo json_encode(['ok' => false, 'error' => 'Could not save upload']);
  exit;
}

// URL construction (works if uploads/ is web-accessible)
$scheme = (!empty($_SERVER['HTTPS']) && $_SERVER['HTTPS'] !== 'off') ? 'https' : 'http';
$host = $_SERVER['HTTP_HOST'] ?? 'localhost';
$path = rtrim(str_replace(DIRECTORY_SEPARATOR, '/', dirname($_SERVER['SCRIPT_NAME'] ?? '')), '/');

$uploadsPublic = ($path !== '' ? $path : '');
$publicPath = $uploadsPublic . '/uploads' . ($folder !== '' ? '/' . $folder : '') . '/' . $filename;
$url = $scheme . '://' . $host . $publicPath;

echo json_encode([
  'ok' => true,
  'filename' => $filename,
  'mime' => $mime,
  'size' => $size,
  'url' => $url,
]);

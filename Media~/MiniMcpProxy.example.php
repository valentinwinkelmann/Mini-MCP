<?php
declare(strict_types=1);

/*
Single-file HTTPS MCP proxy for private ChatGPT use.

What this does:
- serves the MCP endpoint under a secret path segment such as /mcp/my-secret/
- proxies requests to the home relay and passes the upstream secret as both header and query parameter
- avoids OAuth entirely for simpler private use over HTTPS

Deploy this file behind HTTPS and route these paths to it:
- /mcp/<public-secret>/
*/

const MCP_PUBLIC_BASE_URL = 'https://vwgame.dev/mcp';
const UPSTREAM_MCP_URL = 'http://your-myfritz-host:8443/mcp';
const UPSTREAM_SECRET = 'replace-with-a-long-random-secret';
const PUBLIC_PATH_SECRET = 'replace-with-a-long-random-public-path-secret';
const UPSTREAM_IP_RESOLVE = CURL_IPRESOLVE_V4;

handleRequest();

function handleRequest(): void
{
    $route = getRoutePath();
    $method = $_SERVER['REQUEST_METHOD'] ?? 'GET';
    $expectedPrefix = '/' . trim(PUBLIC_PATH_SECRET, '/') . '/';

    if (!requestUsesExpectedSecretPath($route)) {
        respondJson(404, ['error' => 'Not found']);
        return;
    }

    if ($method === 'GET' && ($route === $expectedPrefix . 'health' || $route === rtrim($expectedPrefix, '/') . '/health')) {
        respondJson(200, ['ok' => true, 'message' => 'MiniMCP secret-path proxy is running']);
        return;
    }

    if ($method === 'OPTIONS' && ($route === $expectedPrefix || $route === rtrim($expectedPrefix, '/'))) {
        respondPreflight();
        return;
    }

    if (($method === 'GET' || $method === 'POST') && ($route === $expectedPrefix || $route === rtrim($expectedPrefix, '/'))) {
        handleMcpProxyRequest();
        return;
    }

    respondJson(404, ['error' => 'Not found']);
}

function requestUsesExpectedSecretPath(string $route): bool
{
    $normalized = '/' . trim(PUBLIC_PATH_SECRET, '/') . '/';
    return $route === $normalized || $route === rtrim($normalized, '/');
}

function handleMcpProxyRequest(): void
{
    $method = $_SERVER['REQUEST_METHOD'] ?? 'GET';
    $rawBody = file_get_contents('php://input');
    if ($rawBody === false) {
        respondJson(400, ['error' => 'Failed to read request body']);
        return;
    }

    if ($method === 'POST') {
        $payload = json_decode($rawBody, true);
        if (!is_array($payload)) {
            respondJson(400, [
                'jsonrpc' => '2.0',
                'id' => null,
                'error' => ['code' => -32700, 'message' => 'Parse error'],
            ]);
            return;
        }
    }

    proxyToUpstream($method, $rawBody);
}

function proxyToUpstream(string $method, string $rawBody): void
{
    prepareStreamingResponse();

    $upstream = buildUpstreamRequestContext();
    $ch = curl_init($upstream['url']);
    $responseHeaders = [];
    $statusCode = 502;
    $sentHeaders = false;

    $acceptHeader = getRequestHeaderValue('Accept') ?? 'application/json';
    $contentTypeHeader = getRequestHeaderValue('Content-Type') ?? 'application/json';
    $lastEventIdHeader = getRequestHeaderValue('Last-Event-ID');

    $forwardHeaders = [
        'Accept: ' . $acceptHeader,
        'Content-Type: ' . $contentTypeHeader,
        'Cache-Control: no-store',
        'X-MiniMcp-Relay-Secret: ' . UPSTREAM_SECRET,
    ];

    if (!empty($upstream['hostHeader'])) {
        $forwardHeaders[] = 'Host: ' . $upstream['hostHeader'];
    }

    if (is_string($lastEventIdHeader) && $lastEventIdHeader !== '') {
        $forwardHeaders[] = 'Last-Event-ID: ' . $lastEventIdHeader;
    }

    curl_setopt_array($ch, [
        CURLOPT_CUSTOMREQUEST => $method,
        CURLOPT_POSTFIELDS => $method === 'POST' ? $rawBody : '',
        CURLOPT_HTTPHEADER => $forwardHeaders,
        CURLOPT_RETURNTRANSFER => false,
        CURLOPT_HEADER => false,
        CURLOPT_FOLLOWLOCATION => false,
        CURLOPT_HEADERFUNCTION => static function ($curl, string $headerLine) use (&$responseHeaders, &$statusCode, &$sentHeaders): int {
            $trimmed = trim($headerLine);
            $length = strlen($headerLine);

            if ($trimmed === '') {
                if (!$sentHeaders) {
                    flushUpstreamHeaders($statusCode, $responseHeaders);
                    $sentHeaders = true;
                }

                return $length;
            }

            if (stripos($trimmed, 'HTTP/') === 0) {
                $responseHeaders = [];
                if (preg_match('/^HTTP\/\S+\s+(\d{3})\b/', $trimmed, $matches) === 1) {
                    $statusCode = (int) $matches[1];
                }

                return $length;
            }

            $parts = explode(':', $trimmed, 2);
            if (count($parts) === 2) {
                $responseHeaders[] = [trim($parts[0]), trim($parts[1])];
            }

            return $length;
        },
        CURLOPT_WRITEFUNCTION => static function ($curl, string $chunk) use (&$sentHeaders, &$statusCode, &$responseHeaders): int {
            if (!$sentHeaders) {
                flushUpstreamHeaders($statusCode, $responseHeaders);
                $sentHeaders = true;
            }

            echo $chunk;
            flushOutputBuffers();
            return strlen($chunk);
        },
        CURLOPT_TIMEOUT => 30,
        CURLOPT_CONNECTTIMEOUT => 10,
        CURLOPT_HTTP_VERSION => CURL_HTTP_VERSION_1_1,
        CURLOPT_IPRESOLVE => UPSTREAM_IP_RESOLVE,
    ]);

    $response = curl_exec($ch);
    if ($response === false) {
        $errno = curl_errno($ch);
        $message = curl_error($ch) ?: 'Unknown upstream error';
        curl_close($ch);
        respondJson(502, [
            'error' => 'Upstream relay request failed',
            'details' => $message,
            'curlErrno' => $errno,
            'upstreamUrl' => $upstream['url'],
            'upstreamHost' => $upstream['hostHeader'],
            'resolvedIpv4' => $upstream['resolvedIpv4'],
        ]);
        return;
    }

    if (!$sentHeaders) {
        $statusCode = curl_getinfo($ch, CURLINFO_RESPONSE_CODE) ?: $statusCode;
        flushUpstreamHeaders($statusCode, $responseHeaders);
    }

    curl_close($ch);
}

function buildUpstreamUrl(): string
{
    $separator = strpos(UPSTREAM_MCP_URL, '?') === false ? '?' : '&';
    return UPSTREAM_MCP_URL . $separator . 'secret=' . rawurlencode(trim(UPSTREAM_SECRET));
}

function buildUpstreamRequestContext(): array
{
    $upstreamUrl = buildUpstreamUrl();
    $parts = parse_url($upstreamUrl);
    if (!is_array($parts) || empty($parts['host'])) {
        return [
            'url' => $upstreamUrl,
            'hostHeader' => null,
            'resolvedIpv4' => null,
        ];
    }

    $host = (string) $parts['host'];
    $resolvedIpv4 = resolveIpv4Address($host);
    if ($resolvedIpv4 === null) {
        return [
            'url' => $upstreamUrl,
            'hostHeader' => null,
            'resolvedIpv4' => null,
        ];
    }

    $authority = $resolvedIpv4;
    if (isset($parts['port'])) {
        $authority .= ':' . (int) $parts['port'];
    }

    $rebuilt = ($parts['scheme'] ?? 'http') . '://' . $authority . ($parts['path'] ?? '/');
    if (isset($parts['query']) && $parts['query'] !== '') {
        $rebuilt .= '?' . $parts['query'];
    }

    return [
        'url' => $rebuilt,
        'hostHeader' => $host,
        'resolvedIpv4' => $resolvedIpv4,
    ];
}

function resolveIpv4Address(string $host): ?string
{
    if (filter_var($host, FILTER_VALIDATE_IP, FILTER_FLAG_IPV4)) {
        return $host;
    }

    $resolved = gethostbynamel($host);
    if (!is_array($resolved) || $resolved === []) {
        return null;
    }

    foreach ($resolved as $candidate) {
        if (is_string($candidate) && filter_var($candidate, FILTER_VALIDATE_IP, FILTER_FLAG_IPV4)) {
            return $candidate;
        }
    }

    return null;
}

function getRoutePath(): string
{
    $requestPath = parse_url($_SERVER['REQUEST_URI'] ?? '/', PHP_URL_PATH);
    $scriptBase = rtrim(str_replace('\\', '/', dirname($_SERVER['SCRIPT_NAME'] ?? '/')), '/');
    if (!is_string($requestPath) || $requestPath === '') {
        return '/';
    }

    if ($scriptBase !== '' && $scriptBase !== '/' && str_starts_with($requestPath, $scriptBase)) {
        $requestPath = substr($requestPath, strlen($scriptBase)) ?: '/';
    }

    return $requestPath === '' ? '/' : $requestPath;
}

function extractHeaderValue(string $rawHeaders, string $headerName): ?string
{
    $lines = preg_split('/\r\n|\n|\r/', $rawHeaders) ?: [];
    foreach ($lines as $line) {
        if (stripos($line, $headerName . ':') === 0) {
            return trim(substr($line, strlen($headerName) + 1));
        }
    }

    return null;
}

function getRequestHeaderValue(string $headerName): ?string
{
    $serverKey = 'HTTP_' . strtoupper(str_replace('-', '_', $headerName));
    if ($headerName === 'Content-Type') {
        $serverKey = 'CONTENT_TYPE';
    }

    $value = $_SERVER[$serverKey] ?? null;
    if (!is_string($value)) {
        return null;
    }

    $trimmed = trim($value);
    return $trimmed === '' ? null : $trimmed;
}

function respondPreflight(): void
{
    http_response_code(204);
    header('Access-Control-Allow-Origin: *');
    header('Access-Control-Allow-Methods: GET, POST, OPTIONS');
    header('Access-Control-Allow-Headers: Content-Type, Accept, Last-Event-ID');
    header('Access-Control-Max-Age: 86400');
}

function prepareStreamingResponse(): void
{
    @ini_set('zlib.output_compression', '0');
    @ini_set('output_buffering', '0');
    @ini_set('implicit_flush', '1');

    while (ob_get_level() > 0) {
        ob_end_flush();
    }

    ob_implicit_flush(true);
}

function flushUpstreamHeaders(int $statusCode, array $responseHeaders): void
{
    if (headers_sent()) {
        return;
    }

    http_response_code($statusCode > 0 ? $statusCode : 502);

    $sentContentType = false;
    foreach ($responseHeaders as [$name, $value]) {
        $normalized = strtolower($name);
        if (in_array($normalized, ['content-length', 'transfer-encoding', 'connection'], true)) {
            continue;
        }

        if ($normalized === 'content-type') {
            $sentContentType = true;
        }

        header($name . ': ' . $value, true);
    }

    if (!$sentContentType) {
        header('Content-Type: application/json; charset=utf-8', true);
    }

    header('Cache-Control: no-store', true);
    header('X-Accel-Buffering: no', true);
}

function flushOutputBuffers(): void
{
    if (function_exists('fastcgi_finish_request')) {
        flush();
        return;
    }

    @ob_flush();
    flush();
}

function respondJson(int $status, array $payload): void
{
    http_response_code($status);
    header('Content-Type: application/json; charset=utf-8');
    header('Cache-Control: no-store');
    echo json_encode($payload, JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE);
}
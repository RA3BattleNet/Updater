from flask import Flask, send_file, send_from_directory, abort, request, jsonify
import os
import sys
import argparse
import threading
import time

app = Flask(__name__)

total_in_bytes = 0
total_out_bytes = 0
last_in_bytes = 0
last_out_bytes = 0
current_in_bandwidth = 0
current_out_bandwidth = 0

SERVER_DIR = None

def format_bytes(size):
    for unit in ['B', 'KB', 'MB', 'GB', 'TB']:
        if size < 1024.0:
            return f"{size:.2f} {unit}"
        size /= 1024.0
    return f"{size:.2f} PB"

def bandwidth_monitor():
    global last_in_bytes, last_out_bytes, current_in_bandwidth, current_out_bandwidth
    while True:
        time.sleep(1)
        current_in_bandwidth = total_in_bytes - last_in_bytes
        current_out_bandwidth = total_out_bytes - last_out_bytes
        last_in_bytes = total_in_bytes
        last_out_bytes = total_out_bytes

threading.Thread(target=bandwidth_monitor, daemon=True).start()

def secure_path(base_dir, filename):
    base_dir = os.path.abspath(base_dir)
    requested_path = os.path.abspath(os.path.join(base_dir, filename))
    if not requested_path.startswith(base_dir):
        return None
    return requested_path

@app.route('/')
def index():
    return f"""
    <h1>增量更新本地测试服务器</h1>
    <ul>
        <li><a href="/manifest.xml">manifest.xml</a></li>
        <li><a href="/patches.json">patches.json</a></li>
        <li><a href="/download/">download 根目录</a></li>
    </ul>
    <p>下载格式: <code>/download/files/&lt;md5&gt;</code> 或 <code>/download/patches/&lt;guid&gt;</code></p>
    <p>服务目录: <code>{SERVER_DIR}</code></p>
    <hr>
    <div id="stats">
        <b>download 路由流量统计：</b><br>
        入站: {total_in_bytes} B ({format_bytes(total_in_bytes)})<br>
        出站: {total_out_bytes} B ({format_bytes(total_out_bytes)})<br>
        入站带宽: {format_bytes(current_in_bandwidth)}/s<br>
        出站带宽: {format_bytes(current_out_bandwidth)}/s<br>
    </div>
    <button onclick="resetStats()">清零</button>
    <script>
    function updateStats() {{
        fetch('/stats')
            .then(r => r.json())
            .then(d => {{
                document.getElementById('stats').innerHTML = `
                    <b>download 路由流量统计：</b><br>
                    入站: ${{d.total_in_bytes}} B (${{d.total_in_human}})<br>
                    出站: ${{d.total_out_bytes}} B (${{d.total_out_human}})<br>
                    入站带宽: ${{d.in_bandwidth_human}}/s<br>
                    出站带宽: ${{d.out_bandwidth_human}}/s<br>
                `;
            }});
    }}
    function resetStats() {{ fetch('/reset_stats', {{method: 'POST'}}); }}
    setInterval(updateStats, 2000);
    </script>
    """

@app.route('/download/<path:filename>')
def download_file(filename):
    global total_in_bytes, total_out_bytes
    safe_path = secure_path(SERVER_DIR, filename)
    if safe_path is None or not os.path.isfile(safe_path):
        abort(404)
    total_in_bytes += request.content_length or 0
    total_out_bytes += os.path.getsize(safe_path)
    return send_from_directory(SERVER_DIR, filename, as_attachment=False)

@app.route('/manifest.xml')
def serve_xml():
    path = os.path.join(SERVER_DIR, 'manifest.xml')
    if not os.path.isfile(path):
        abort(404)
    return send_file(path, mimetype='application/xml')

@app.route('/patches.json')
def serve_json():
    path = os.path.join(SERVER_DIR, 'patches.json')
    if not os.path.isfile(path):
        abort(404)
    return send_file(path, mimetype='application/json')

@app.route('/reset_stats', methods=['POST'])
def reset_stats():
    global total_in_bytes, total_out_bytes, last_in_bytes, last_out_bytes
    total_in_bytes = total_out_bytes = last_in_bytes = last_out_bytes = 0
    return jsonify({"status": "ok"})

@app.route('/stats')
def stats():
    return {
        "total_in_bytes": total_in_bytes,
        "total_out_bytes": total_out_bytes,
        "current_in_bandwidth": current_in_bandwidth,
        "current_out_bandwidth": current_out_bandwidth,
        "total_in_human": format_bytes(total_in_bytes),
        "total_out_human": format_bytes(total_out_bytes),
        "in_bandwidth_human": format_bytes(current_in_bandwidth),
        "out_bandwidth_human": format_bytes(current_out_bandwidth),
    }

if __name__ == '__main__':
    parser = argparse.ArgumentParser(description='增量更新本地测试 HTTP 服务器')
    parser.add_argument('--port', type=int, default=23456)
    parser.add_argument('--dir', type=str, required=True,
                        help='服务目录，需包含 manifest.xml、patches.json、files/、patches/')
    args = parser.parse_args()

    SERVER_DIR = os.path.abspath(args.dir)
    if not os.path.isdir(SERVER_DIR):
        print(f"错误：目录不存在: {SERVER_DIR}")
        sys.exit(1)

    local_addr = "127.0.0.1"
    print(f"=== 增量更新本地测试服务器 ===")
    print(f"服务目录: {SERVER_DIR}")
    print(f"地址:     http://{local_addr}:{args.port}")
    print(f"Manifest: http://{local_addr}:{args.port}/manifest.xml")
    print(f"Patches:  http://{local_addr}:{args.port}/patches.json")
    print(f"Download: http://{local_addr}:{args.port}/download/...")
    app.run(debug=True, host=local_addr, port=args.port)

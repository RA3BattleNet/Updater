from flask import Flask, send_file, send_from_directory, abort, request, jsonify
import os
from pathlib import Path
import threading
import time

app = Flask(__name__)

# 只统计 /download 路由的流量
total_in_bytes = 0
total_out_bytes = 0
last_in_bytes = 0
last_out_bytes = 0
current_in_bandwidth = 0
current_out_bandwidth = 0
def format_bytes(size):
    # 转换为易读单位
    for unit in ['字节', 'KB', 'MB', 'GB', 'TB']:
        if size < 1024.0:
            return f"{size:.2f} {unit}"
        size /= 1024.0
    return f"{size:.2f} PB"

def bandwidth_monitor():
    global last_in_bytes, last_out_bytes, current_in_bandwidth, current_out_bandwidth
    while True:
        time.sleep(0.2)
        # 计算最近一秒的带宽
        current_in_bandwidth = total_in_bytes - last_in_bytes
        current_out_bandwidth = total_out_bytes - last_out_bytes
        last_in_bytes = total_in_bytes
        last_out_bytes = total_out_bytes

# 启动带宽监控线程
threading.Thread(target=bandwidth_monitor, daemon=True).start()

def secure_path(base_dir, filename):
    base_dir = os.path.abspath(base_dir)
    requested_path = os.path.abspath(os.path.join(base_dir, filename))
    if not requested_path.startswith(base_dir):
        return None
    return requested_path

XML_FILE_PATH = r'C:\Users\kt\Downloads\test\server\ori\CoronaLauncher_Setup_3.12.9381.2215.xml'
JSON_FILE_PATH = r'C:\Users\kt\Downloads\test\server\patch\patches.json'
DOWNLOAD_DIR = r'C:\Users\kt\Downloads\test\server\patch'
FILES_DOWNLOAD_DIR = r'C:\Users\kt\Downloads\test\server\patch\files'
PATCHES_DOWNLOAD_DIR = r'C:\Users\kt\Downloads\test\server\patch\patches'

@app.route('/')
def index():
    return f"""
    <h1>本地文件服务测试</h1>
    <ul>
        <li><a href="/manifest.xml">查看XML文件</a></li>
        <li><a href="/patches.json">查看JSON文件</a></li>
        <li><a href="/download/">文件下载请求</a></li>
    </ul>
    <p>使用 <code>/download/&lt;类型&gt;/文件名</code> 格式下载文件</p>
    <hr>
    <div id="stats">
        <b>仅统计 /download 路由：</b><br>
        入站流量（请求体）：{total_in_bytes} 字节（{format_bytes(total_in_bytes)}）<br>
        出站流量（响应体）：{total_out_bytes} 字节（{format_bytes(total_out_bytes)}）<br>
        <b>实时带宽：</b><br>
        入站带宽：{current_in_bandwidth} 字节/秒（{format_bytes(current_in_bandwidth)}/秒）<br>
        出站带宽：{current_out_bandwidth} 字节/秒（{format_bytes(current_out_bandwidth)}/秒）<br>
    </div>
    <button onclick="resetStats()">流量计数清零</button>
    <script>
    function updateStats() {{
        fetch('/stats')
            .then(response => response.json())
            .then(data => {{
                document.getElementById('stats').innerHTML = `
                    <b>仅统计 /download 路由：</b><br>
                    入站流量（请求体）：${{data.total_in_bytes}} 字节（${{data.total_in_human}}）<br>
                    出站流量（响应体）：${{data.total_out_bytes}} 字节（${{data.total_out_human}}）<br>
                    <b>实时带宽：</b><br>
                    入站带宽：${{data.current_in_bandwidth}} 字节/秒（${{data.in_bandwidth_human}}/秒）<br>
                    出站带宽：${{data.current_out_bandwidth}} 字节/秒（${{data.out_bandwidth_human}}/秒）<br>
                `;
            }});
    }}
    function resetStats() {{
        fetch('/reset_stats', {{method: 'POST'}})
            .then(response => response.json())
            .then(data => {{
                updateStats();
            }});
    }}
    setInterval(updateStats, 2000);
    </script>
    """

@app.route('/download/<path:filename>')
def download_file(filename):
    global total_in_bytes, total_out_bytes
    try:
        safe_path = secure_path(DOWNLOAD_DIR, filename)
        if safe_path is None:
            abort(404)
    except:
        abort(404)

    if not os.path.exists(safe_path) or not os.path.isfile(safe_path):
        abort(404)

    # 入站流量（请求体长度，GET为0，POST可用request.content_length）
    total_in_bytes += request.content_length or 0
    # 出站流量（文件大小）
    total_out_bytes += os.path.getsize(safe_path)

    return send_from_directory(
        DOWNLOAD_DIR,
        filename,
        as_attachment=False
    )

@app.route('/manifest.xml')
def serve_xml():
    if not os.path.exists(XML_FILE_PATH):
        abort(404)
    try:
        return send_file(XML_FILE_PATH, mimetype='application/xml')
    except FileNotFoundError:
        abort(404)

@app.route('/patches.json')
def serve_json():
    if not os.path.exists(JSON_FILE_PATH):
        abort(404)
    try:
        return send_file(JSON_FILE_PATH, mimetype='application/json')
    except FileNotFoundError:
        abort(404)

@app.route('/reset_stats', methods=['POST'])
def reset_stats():
    global total_in_bytes, total_out_bytes, last_in_bytes, last_out_bytes
    total_in_bytes = 0
    total_out_bytes = 0
    last_in_bytes = 0
    last_out_bytes = 0
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
        "out_bandwidth_human": format_bytes(current_out_bandwidth)
    }

if __name__ == '__main__':
    local_addr="127.0.0.1"
    local_port=23456
    print("启动本地文件服务...")
    print(f"访问 http://{local_addr}:{local_port}/ 查看服务")
    print(f"XML文件: http://{local_addr}:{local_port}/manifest.xml")
    print(f"JSON文件: http://{local_addr}:{local_port}/patches.json")
    app.run(debug=True, host=local_addr,port=local_port)
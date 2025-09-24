from flask import Flask, send_file, send_from_directory, abort
import os
from pathlib import Path

# 替代 safe_join 的方法
def secure_path(base_dir, filename):
    # 规范化路径并确保它在基础目录内
    base_dir = os.path.abspath(base_dir)
    requested_path = os.path.abspath(os.path.join(base_dir, filename))
    
    # 检查请求的路径是否在基础目录内
    if not requested_path.startswith(base_dir):
        return None
    
    return requested_path

# 然后在 download_file 函数中使用：

app = Flask(__name__)

# 配置文件路径

# XML_FILE_PATH = r'C:\Users\kt\Downloads\test\server\ori\CoronaLauncher_Setup_3.12.9269.19502.xml'
# JSON_FILE_PATH = r'C:\Users\kt\Downloads\test\server\patch\patches.json'
# DOWNLOAD_DIR = r'C:\Users\kt\Downloads\test\server\patch'
# FILES_DOWNLOAD_DIR = r'C:\Users\kt\Downloads\test\server\patch\files'
# PATCHES_DOWNLOAD_DIR = r'C:\Users\kt\Downloads\test\server\patch\patches'
# # Path(DOWNLOAD_DIR).mkdir(exist_ok=True)
# # Path(FILES_DOWNLOAD_DIR).mkdir(exist_ok=True)
# # Path(PATCHES_DOWNLOAD_DIR).mkdir(exist_ok=True)


XML_FILE_PATH = r'C:\Users\kt\Downloads\test\server\ori\CoronaLauncher_Setup_3.12.9381.2215.xml'
JSON_FILE_PATH = r'C:\Users\kt\Downloads\test\server\patch\patches.json'
DOWNLOAD_DIR = r'C:\Users\kt\Downloads\test\server\patch'
FILES_DOWNLOAD_DIR = r'C:\Users\kt\Downloads\test\server\patch\files'
PATCHES_DOWNLOAD_DIR = r'C:\Users\kt\Downloads\test\server\patch\patches'
# Path(DOWNLOAD_DIR).mkdir(exist_ok=True)
# Path(FILES_DOWNLOAD_DIR).mkdir(exist_ok=True)
# Path(PATCHES_DOWNLOAD_DIR).mkdir(exist_ok=True)

@app.route('/')
def index():
    return """
    <h1>本地文件服务测试</h1>
    <ul>
        <li><a href="/manifest.xml">查看XML文件</a></li>
        <li><a href="/patches.json">查看JSON文件</a></li>
        <li><a href="/download/">文件下载请求</a></li>
    </ul>
    <p>使用 <code>/download/<类型>/文件名</code> 格式下载文件</p>
    """

@app.route('/manifest.xml')
def serve_xml():
    """提供XML文件"""
    if not os.path.exists(XML_FILE_PATH):
        abort(404)
    try:
        return send_file(XML_FILE_PATH, mimetype='application/xml')
    except FileNotFoundError:
        abort(404)

@app.route('/patches.json')
def serve_json():
    """提供JSON文件"""
    if not os.path.exists(JSON_FILE_PATH):
        abort(404)
    try:
        return send_file(JSON_FILE_PATH, mimetype='application/json')
    except FileNotFoundError:
        abort(404)

@app.route('/download/<path:filename>')
def download_file(filename):
    """提供文件下载"""
    try:
        safe_path = secure_path(DOWNLOAD_DIR, filename)
        if safe_path is None:
            abort(404)
    except:
        abort(404)

    if not os.path.exists(safe_path) or not os.path.isfile(safe_path):
        abort(404)
    
    return send_from_directory(
        DOWNLOAD_DIR, 
        filename, 
        as_attachment=False
    )

if __name__ == '__main__':
    local_addr="127.0.0.1"
    local_port=23456
    print("启动本地文件服务...")
    print(f"访问 http://{local_addr}:{local_port}/ 查看服务")
    print(f"XML文件: http://{local_addr}:{local_port}/manifest.xml")
    print(f"JSON文件: http://{local_addr}:{local_port}/patches.json")
    app.run(debug=True, host=local_addr,port=local_port)
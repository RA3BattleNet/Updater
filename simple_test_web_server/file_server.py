"""本地测试用静态文件服务器（无业务逻辑，只发文件）。

目录布局（与服务端发布物一致）：
    <dir>/manifest.xml
    <dir>/files/{md5}
    <dir>/patches/{oldHash}_{newHash}.hdiff

Flask 的 send_file(conditional=True) 自带 ETag / If-None-Match / Range 支持。
"""

import argparse
import os
import sys

from flask import Flask, abort, request, send_file

app = Flask(__name__)
SERVER_DIR = None


@app.route("/", defaults={"filename": "manifest.xml"})
@app.route("/<path:filename>")
def serve(filename):
    path = os.path.normpath(os.path.join(SERVER_DIR, filename))
    if not os.path.abspath(path).startswith(os.path.abspath(SERVER_DIR)):
        abort(404)
    if not os.path.isfile(path):
        abort(404)
    return send_file(path, conditional=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="增量更新本地测试静态服务器")
    parser.add_argument("--port", type=int, default=23456)
    parser.add_argument(
        "--dir",
        type=str,
        required=True,
        help="服务目录，需包含 manifest.xml、files/、patches/",
    )
    parser.add_argument("--host", type=str, default="127.0.0.1")
    args = parser.parse_args()

    SERVER_DIR = os.path.abspath(args.dir)
    if not os.path.isdir(SERVER_DIR):
        print(f"错误：目录不存在: {SERVER_DIR}")
        sys.exit(1)

    print("=== 增量更新本地测试服务器 ===")
    print(f"服务目录: {SERVER_DIR}")
    print(f"Manifest: http://{args.host}:{args.port}/manifest.xml")
    app.run(debug=False, host=args.host, port=args.port)

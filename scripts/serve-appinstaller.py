import http.server
import os
import pathlib
import re
import sys


class AppInstallerHandler(http.server.SimpleHTTPRequestHandler):
    extensions_map = {
        **http.server.SimpleHTTPRequestHandler.extensions_map,
        ".appinstaller": "application/appinstaller",
        ".msix": "application/msix",
    }

    def end_headers(self):
        self.send_header("Accept-Ranges", "bytes")
        super().end_headers()

    def send_head(self):
        path = self.translate_path(self.path)
        range_header = self.headers.get("Range")
        if not range_header or not os.path.isfile(path):
            self._byte_range = None
            return super().send_head()

        match = re.fullmatch(r"bytes=(\d*)-(\d*)", range_header.strip())
        if not match or (not match.group(1) and not match.group(2)):
            self.send_error(http.HTTPStatus.REQUESTED_RANGE_NOT_SATISFIABLE)
            return None

        file_size = os.path.getsize(path)
        if match.group(1):
            start = int(match.group(1))
            end = int(match.group(2)) if match.group(2) else file_size - 1
        else:
            suffix_length = int(match.group(2))
            start = max(0, file_size - suffix_length)
            end = file_size - 1
        end = min(end, file_size - 1)
        if start > end or start >= file_size:
            self.send_response(http.HTTPStatus.REQUESTED_RANGE_NOT_SATISFIABLE)
            self.send_header("Content-Range", f"bytes */{file_size}")
            self.end_headers()
            return None

        file = open(path, "rb")
        stat = os.fstat(file.fileno())
        self._byte_range = (start, end)
        self.send_response(http.HTTPStatus.PARTIAL_CONTENT)
        self.send_header("Content-Type", self.guess_type(path))
        self.send_header("Content-Range", f"bytes {start}-{end}/{file_size}")
        self.send_header("Content-Length", str(end - start + 1))
        self.send_header("Last-Modified", self.date_time_string(stat.st_mtime))
        self.end_headers()
        return file

    def copyfile(self, source, outputfile):
        byte_range = getattr(self, "_byte_range", None)
        if byte_range is None:
            return super().copyfile(source, outputfile)
        start, end = byte_range
        source.seek(start)
        remaining = end - start + 1
        while remaining:
            chunk = source.read(min(64 * 1024, remaining))
            if not chunk:
                break
            outputfile.write(chunk)
            remaining -= len(chunk)


if len(sys.argv) != 3:
    raise SystemExit("usage: serve-appinstaller.py <port> <directory>")

port = int(sys.argv[1])
directory = pathlib.Path(sys.argv[2]).resolve()
handler = lambda *args, **kwargs: AppInstallerHandler(*args, directory=str(directory), **kwargs)
server = http.server.ThreadingHTTPServer(("127.0.0.1", port), handler)
server.serve_forever()

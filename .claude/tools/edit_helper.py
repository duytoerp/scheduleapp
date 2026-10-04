"""Thay chuỗi trong file, giữ nguyên BOM và kiểu xuống dòng (CRLF/LF). Dùng: edit(path, [(cũ, mới), ...]) — báo lỗi nếu không thấy chuỗi cũ, không ghi gì."""
def edit(p, pairs):
    b=open(p,'rb').read(); bom=b.startswith(b'\xef\xbb\xbf'); s=b.decode('utf-8-sig')
    crlf='\r\n' in s; s=s.replace('\r\n','\n')
    for old,new in pairs:
        assert old in s, (p, old[:80])
        s=s.replace(old,new,1)
    if crlf: s=s.replace('\n','\r\n')
    open(p,'wb').write((b'\xef\xbb\xbf' if bom else b'')+s.encode('utf-8'))

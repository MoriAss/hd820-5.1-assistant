"""Render the public README into a portable offline guide; never edits program code."""
from pathlib import Path
from markdown_it import MarkdownIt
from bs4 import BeautifulSoup
from urllib.parse import unquote
import html,re

HERE=Path(__file__).resolve().parents[1]
PUBLIC=HERE
body=MarkdownIt('commonmark',{'html':True}).enable('table').render((PUBLIC/'README.md').read_text(encoding='utf-8'))
soup=BeautifulSoup(body,'html.parser')
for heading in soup.find_all(re.compile('^h[1-6]$')):
    heading['id']=re.sub(r'[^\w\u4e00-\u9fff\- ]','',heading.get_text().lower()).replace(' ','-')
for table in soup.find_all('table'):
    labels=[cell.get_text(' ',strip=True) for cell in table.find_all('th')]
    for row in table.find_all('tr'):
        for at,cell in enumerate(row.find_all('td',recursive=False)):
            if at<len(labels):cell['data-label']=labels[at]
    wrapper=soup.new_tag('div',attrs={'class':'table-scroll','tabindex':'0'})
    table.wrap(wrapper)
for img in soup.find_all('img'):
    img['loading']='eager'
    path=PUBLIC/img['src']
    assert path.is_file(),path
    img.wrap(soup.new_tag('a',href=img['src'],attrs={'title':'打开原图查看细节'}))
for link in soup.find_all('a',href=True):
    href=link['href']
    if href.startswith('#'):
        assert soup.find(id=unquote(href[1:])),'Missing anchor '+href
    elif not re.match(r'https?://',href):
        assert (PUBLIC/unquote(href.split('#')[0])).is_file(),'Missing local link '+href
css='''
:root{color-scheme:light;font-family:"Segoe UI","Microsoft YaHei",Arial,sans-serif;color:#17243b;background:#eef3f9}
*{box-sizing:border-box}body{margin:0;padding:28px 16px 64px;font-size:17px;line-height:1.8;overflow-wrap:anywhere}
main{max-width:1000px;margin:auto;background:white;padding:40px 48px;border-radius:16px;border:1px solid #dbe5f0}
h1{font-size:38px;line-height:1.3;margin:0 0 18px}h2{font-size:25px;line-height:1.4;border-top:1px solid #dbe5f0;padding-top:30px;margin-top:38px;scroll-margin-top:20px}
a{color:#175bc3;text-underline-offset:3px}a:hover{color:#0c3574}p,li{max-width:100%}li{padding:3px 0}
img{max-width:100%;height:auto;display:block;margin:24px auto;border:1px solid #e0e6ef;border-radius:10px}
em{color:#52627b;font-size:14px}.table-scroll{max-width:100%;overflow-x:auto;margin:20px 0}
table{width:100%;border-collapse:collapse;font-size:15px;line-height:1.7}th,td{padding:12px 14px;border:1px solid #dce4ee;text-align:left;vertical-align:top}th{background:#eff4fb;color:#233e64}
code{font-family:Consolas,"Microsoft YaHei",monospace;font-size:.88em;background:#edf2f8;border-radius:4px;padding:2px 5px;overflow-wrap:anywhere}
pre{overflow-x:auto;background:#142238;color:#edf4ff;border-radius:10px;padding:20px;line-height:1.7;font-size:14px}pre code{background:none;color:inherit;padding:0;white-space:pre;overflow-wrap:normal}
strong{font-weight:650}nav{display:flex;gap:12px;flex-wrap:wrap;margin:0 0 25px;padding-bottom:15px;border-bottom:1px solid #e0e6ef;font-size:14px}
@media(max-width:600px){body{padding:0;font-size:16px}main{padding:26px 18px;border:0;border-radius:0}h1{font-size:29px}h2{font-size:22px}table,tbody,tr,td{display:block;width:100%}thead{display:none}tr{border:1px solid #dce4ee;border-radius:8px;margin:12px 0;overflow:hidden}td{border:0;padding:10px 12px}td+td{border-top:1px solid #eef2f7}td:before{content:attr(data-label);display:block;color:#52627b;font-size:13px;font-weight:600;margin-bottom:4px}pre{padding:14px}}
@media print{body{background:white;padding:0}main{border:0;padding:0;max-width:none}nav{display:none}a{color:inherit}h2{break-after:avoid}img{max-height:760px;object-fit:contain}tr{break-inside:avoid}}
'''
document='<!doctype html><html lang="zh-CN"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>HD820 5.1 助手 · 开始使用</title><style>'+css+'</style><main><nav><a href="https://github.com/MoriAss/hd820-5.1-assistant">项目主页</a><a href="#遇到问题先查这里">直接看排错表</a><span>图文教程可离线阅读；下载安装需要联网</span></nav>'+str(soup)+'</main></html>'
(PUBLIC/'开始使用.html').write_text(document,encoding='utf-8',newline='\n')
print(PUBLIC/'开始使用.html')

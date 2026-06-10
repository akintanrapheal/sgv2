import json, html, re
DB = r"C:\Users\DELL\AppData\Local\Temp\wp_database.sql"
BACKSLASH = bytes([92]); QUOTE = b"'"
# MySQL string-literal escapes — translate so \r\n becomes real CR/LF (not the letters "rn")
ESCAPES = {b'n': b'\n', b'r': b'\r', b't': b'\t', b'0': b'\x00', b'b': b'\x08', b'Z': b'\x1a'}

def tuples_for(table_bytes, data):
    marker = b"INSERT INTO `" + table_bytes + b"` VALUES "
    i = 0; n = len(data)
    while True:
        j = data.find(marker, i)
        if j < 0: break
        k = j + len(marker)
        while k < n and data[k:k+1] != b';':
            if data[k:k+1] != b'(':
                k += 1; continue
            k += 1; fields = []; cur = bytearray(); inq = False; isnull = True
            while k < n:
                c = data[k:k+1]
                if inq:
                    if c == BACKSLASH:
                        nxt = data[k+1:k+2]
                        cur += ESCAPES.get(nxt, nxt)   # translate \n \r \t etc. (else keep literal)
                        k += 2; continue
                    if c == QUOTE: inq = False; k += 1; continue
                    cur += c; k += 1; continue
                else:
                    if c == QUOTE: inq = True; isnull = False; k += 1; continue
                    if c == b',':
                        fields.append(None if (isnull and bytes(cur).strip()==b'NULL') else bytes(cur)); cur=bytearray(); isnull=True; k+=1; continue
                    if c == b')':
                        fields.append(None if (isnull and bytes(cur).strip()==b'NULL') else bytes(cur)); k+=1; yield fields; break
                    cur += c; k += 1; continue
            while k < n and data[k:k+1] in b', \n\r': k += 1
        i = k + 1

def dec(b): return b.decode('utf-8','replace') if isinstance(b,(bytes,bytearray)) else b
def strip_html(s):
    if not s: return ''
    s = s.replace('\r\n', '\n').replace('\r', '\n')
    s = re.sub(r'<\s*(br|/p|/div|/li|/h[1-6])\s*/?>', '\n', s, flags=re.I)
    s = re.sub(r'<[^>]*>', ' ', s)
    s = html.unescape(s)
    s = re.sub(r'[ \t]+', ' ', s); s = re.sub(r'\n{3,}', '\n\n', s)
    return '\n'.join(l.strip() for l in s.split('\n')).strip()

with open(DB,'rb') as f: data = f.read()
print("loaded", len(data))

products={}; variations={}; attach_guid={}
for fld in tuples_for(b"SERVMASK_PREFIX_posts", data):
    if len(fld) < 21: continue
    pid=dec(fld[0]); content=dec(fld[4]) or ''; title=dec(fld[5]) or ''; excerpt=dec(fld[6]) or ''
    status=dec(fld[7]) or ''; parent=dec(fld[17]); guid=dec(fld[18]) or ''; ptype=dec(fld[20]) or ''
    if ptype=='product' and status=='publish':
        products[pid]={'id':pid,'title':html.unescape(title),
                       'description':strip_html(content),'short':strip_html(excerpt)}
    elif ptype=='product_variation':
        variations.setdefault(parent,[]).append(pid)
    elif ptype=='attachment':
        attach_guid[pid]=guid
print("products:", len(products), "variations:", sum(len(v) for v in variations.values()))

WANT={b'_sku',b'_regular_price',b'_price',b'_stock',b'_thumbnail_id',b'_product_image_gallery'}
meta={}
for fld in tuples_for(b"SERVMASK_PREFIX_postmeta", data):
    if len(fld) < 4: continue
    pid=dec(fld[1]); key=fld[2]; val=dec(fld[3])
    if key is None: continue
    if key in WANT or key.startswith(b'attribute_'):
        meta.setdefault(pid,{})[dec(key)]=val

term_name={}
for fld in tuples_for(b"SERVMASK_PREFIX_terms", data):
    if len(fld)>=2: term_name[dec(fld[0])]=html.unescape(dec(fld[1]) or '')
tt_cat={}
for fld in tuples_for(b"SERVMASK_PREFIX_term_taxonomy", data):
    if len(fld)>=3 and dec(fld[2])=='product_cat': tt_cat[dec(fld[0])]=term_name.get(dec(fld[1]),'')
obj_cat={}
for fld in tuples_for(b"SERVMASK_PREFIX_term_relationships", data):
    if len(fld)>=2:
        oid=dec(fld[0]); ttid=dec(fld[1])
        if ttid in tt_cat: obj_cat.setdefault(oid,[]).append(tt_cat[ttid])

def images(pid):
    m=meta.get(pid,{}); urls=[]
    tid=m.get('_thumbnail_id')
    if tid and attach_guid.get(tid): urls.append(attach_guid[tid])
    gal=m.get('_product_image_gallery')
    if gal:
        for aid in gal.split(','):
            u=attach_guid.get(aid.strip())
            if u and u not in urls: urls.append(u)
    return urls

def vattrs(vid):
    out={}
    for k,v in meta.get(vid,{}).items():
        if k.startswith('attribute_') and v: out[k.replace('attribute_','').replace('pa_','')]=v
    return out

catalog=[]
for pid,p in products.items():
    m=meta.get(pid,{})
    variants=[]
    for vid in variations.get(pid,[]):
        vm=meta.get(vid,{})
        variants.append({'price':vm.get('_regular_price') or vm.get('_price'),
                         'stock':vm.get('_stock'),'attrs':vattrs(vid)})
    catalog.append({'wc_id':pid,'title':p['title'],'sku':m.get('_sku',''),
        'description':p['description'],'short':p['short'],
        'type':'variable' if variants else 'simple',
        'price':m.get('_regular_price') or m.get('_price'),'stock':m.get('_stock'),
        'categories':obj_cat.get(pid,[]),'images':images(pid),
        'variant_count':len(variants),'variants':variants})

json.dump(catalog, open(r"C:\Users\DELL\AppData\Local\Temp\catalog.json",'w',encoding='utf-8'))
print("wrote catalog.json | products:",len(catalog),
      "| with variants:",sum(1 for c in catalog if c['variant_count']),
      "| with images:",sum(1 for c in catalog if c['images']),
      "| total variants:",sum(c['variant_count'] for c in catalog))

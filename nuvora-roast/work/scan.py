import re,sys,collections
s=open('bundle/main.dart.js',encoding='utf8',errors='replace').read()
print('size',len(s))
def uniq(pat,flags=0,maxn=40,group=0):
    c=collections.Counter(m.group(group) for m in re.finditer(pat,s,flags)); return c
def show(label,c,maxn=40):
    print(f'\n## {label}: {sum(c.values())} hits, {len(c)} unique')
    for k,v in list(c.most_common(maxn)): print('  ',v,repr(k[:150]))
show('URLs',uniq(r'https?://[^\s"\'\\<>)]{4,100}'),60)
show('hosting/BaaS domains',uniq(r'[A-Za-z0-9.-]{0,40}(?:railway\.app|supabase\.co|firebaseio\.com|firebaseapp\.com|amazonaws\.com|appspot\.com|netlify\.app|herokuapp\.com|ngrok[a-z.-]*)'))
show('key shapes',uniq(r'AIza[0-9A-Za-z_-]{20,}|sk_(?:live|test)_[0-9A-Za-z]{6,}|pk_(?:live|test)_[0-9A-Za-z]{6,}|AKIA[0-9A-Z]{12,}|gh[pousr]_[0-9A-Za-z]{20,}|xox[baprs]-[0-9A-Za-z-]{10,}'))
show('JWT shapes',uniq(r'eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{5,}'))
show('secret-ish assignments',uniq(r'(?i)(?:api[_-]?key|secret|password|private[_-]?key|client[_-]?secret)["\']?\s*[:=]\s*["\'][^"\']{4,60}["\']'))
show('TODO/FIXME',uniq(r'(?:TODO|FIXME|HACK)[: ][^"\\\n]{0,60}'))
show('debug/test flags',uniq(r'(?i)kDebugMode|debugShowCheckedModeBanner|test[_ ]?mode|sandbox|staging|localhost|127\.0\.0\.1|10\.0\.2\.2'),20)
show('emails',uniq(r'[A-Za-z0-9._%+-]{2,40}@[A-Za-z0-9.-]{3,40}\.[a-z]{2,}'))
show('UG phone numbers',uniq(r'(?<![0-9])(?:\+?256|0)7[0-9]{2}[ ]?[0-9]{3}[ ]?[0-9]{3}(?![0-9])'))
show('endpoint paths',uniq(r'["\']/(?:auth|account|topup|cards|students|wallets|payments|family|users|schools|diagnostics|webhook|ussd|merchants|reports|analytics)[A-Za-z0-9_/{}.\$-]{0,50}'),70)
show('sourcemap ref',uniq(r'sourceMappingURL=[^\s"]+'))
show('dart2js/flutter markers',uniq(r'Instance of \'[A-Za-z_]+\'|dart2js[^"\s]{0,30}'),8)

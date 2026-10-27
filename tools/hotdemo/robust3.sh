set -u
JIT="F:/Courier/tools/hotdemo/bin/Release/net10.0/hotdemo.exe"
rm -rf "$TEMP/hs4"
echo "=== A: start host, kill it while a client still holds the mapping, restart on same root ==="
"$JIT" host "$TEMP/hs4" 2000000 3000 > /tmp/hs4a.log 2>&1 &
H1=$!
for i in $(seq 1 300); do [ -f "$TEMP/hs4/endpoint.json" ] && break; sleep 0.05; done
sleep 0.3
"$JIT" client "$TEMP/hs4" 5000 > /tmp/hs4c.log 2>&1 &
C1=$!
sleep 1.2
cat "$TEMP/hs4/endpoint.json" | tr -d "\n" | sed "s/  */ /g"; echo
kill -9 $H1 2>/dev/null
sleep 0.2
echo "--- client after host kill:"
wait $C1; echo "client exit=$?"
tail -2 /tmp/hs4c.log
echo "--- restarting host on the SAME root while the dead client mapping may linger:"
timeout 60 "$JIT" host "$TEMP/hs4" 100000 0 > /tmp/hs4b.log 2>&1 &
H2=$!
sleep 3
echo "second host alive? $(kill -0 $H2 2>/dev/null && echo yes || echo no)"
head -3 /tmp/hs4b.log
if [ -f "$TEMP/hs4/endpoint.json" ]; then cat "$TEMP/hs4/endpoint.json" | tr -d "\n" | sed "s/  */ /g"; echo; fi
"$JIT" client "$TEMP/hs4" 300 > /tmp/hs4d.log 2>&1
echo "client-after-restart exit=$?"
tail -1 /tmp/hs4d.log
kill $H2 2>/dev/null
taskkill //F //IM hotdemo.exe > /dev/null 2>&1; true
set -u
AOT="F:/Courier/tools/hotdemo/out/aot/hotdemo.exe"
rm -rf "$TEMP/hs7"
"$AOT" host "$TEMP/hs7" 4000000 > /tmp/hs7.log 2>&1 &
HJ=$!
for i in $(seq 1 300); do [ -f "$TEMP/hs7/endpoint.json" ] && break; sleep 0.05; done
sleep 0.3
echo "=== single-shot echo, 20 sequential, amortized ==="
S=$(date +%s%N); for i in $(seq 1 20); do "$AOT" echo "$TEMP/hs7" > /dev/null 2>&1 || echo -n "FAIL$? "; done; E=$(date +%s%N)
echo "20 echo runs: $(( (E-S)/1000000 )) ms total, $(( (E-S)/20000000 )) ms each"
echo "=== single-shot noop in same loop position, 20 sequential ==="
S=$(date +%s%N); for i in $(seq 1 20); do "$AOT" version > /dev/null 2>&1; done; E=$(date +%s%N)
echo "20 noop runs: $(( (E-S)/1000000 )) ms total, $(( (E-S)/20000000 )) ms each"
kill $HJ 2>/dev/null
taskkill //F //IM hotdemo.exe > /dev/null 2>&1; true
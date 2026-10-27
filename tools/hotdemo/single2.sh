set -u
AOT="F:/Courier/tools/hotdemo/out/aot/hotdemo.exe"
rm -rf "$TEMP/ss1" "$TEMP/ss2"
echo "=== FILE channel: host-file + 20 fileonce calls ==="
"$AOT" host-file "$TEMP/ss1" 200000 > /tmp/ss1.log 2>&1 &
H1=$!
for i in $(seq 1 300); do [ -f "$TEMP/ss1/endpoint.json" ] && break; sleep 0.05; done
sleep 0.3
S=$(date +%s%N); for i in $(seq 1 20); do "$AOT" fileonce "$TEMP/ss1" > /dev/null 2>&1 || echo -n "FAIL "; done; E=$(date +%s%N)
echo "file:  20 runs = $(( (E-S)/1000000 )) ms total, $(( (E-S)/20000000 )) ms each"
kill $H1 2>/dev/null
sleep 0.3
echo "=== HOT channel: host + 20 echo calls ==="
"$AOT" host "$TEMP/ss2" 4000000 > /tmp/ss2.log 2>&1 &
H2=$!
for i in $(seq 1 300); do [ -f "$TEMP/ss2/endpoint.json" ] && break; sleep 0.05; done
sleep 0.3
S=$(date +%s%N); for i in $(seq 1 20); do "$AOT" echo "$TEMP/ss2" > /dev/null 2>&1 || echo -n "FAIL "; done; E=$(date +%s%N)
echo "hot:   20 runs = $(( (E-S)/1000000 )) ms total, $(( (E-S)/20000000 )) ms each"
kill $H2 2>/dev/null
taskkill //F //IM hotdemo.exe > /dev/null 2>&1; true
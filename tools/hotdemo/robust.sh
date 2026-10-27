AOT="F:/Courier/tools/hotdemo/out/aot/hotdemo.exe"
R="$TEMP/stale1"; rm -rf "$R"
echo "=== test 1: stale section while an old client still holds it ==="
"$AOT" host "$R" 2000000 > /tmp/sh1.log 2>&1 &
for i in $(seq 1 300); do [ -f "$R/endpoint.json" ] && break; sleep 0.05; done
HP=$(sed -n 's/.*"pid": *\([0-9]*\).*/\1/p' "$R/endpoint.json")
# long-running client keeps the section handle open
timeout 60 "$AOT" client "$R" 400000 > /tmp/sh2.log 2>&1 &
CP=$!
sleep 0.8
echo "killing host $HP while client $CP still holds the mapping"
taskkill //F //PID $HP > /dev/null 2>&1
sleep 0.2
echo "--- new host on the same root: ---"
timeout 10 "$AOT" host "$R" 2000000 > /tmp/sh3.log 2>&1
echo "new-host exit=$?"
cat /tmp/sh3.log | head -8
kill $CP 2>/dev/null; wait $CP 2>/dev/null
echo
echo "=== test 2: slot exhaustion (32 slots, 40 clients) ==="
R2="$TEMP/stale2"; rm -rf "$R2"
"$AOT" host "$R2" 2000000 > /tmp/si1.log 2>&1 &
for i in $(seq 1 300); do [ -f "$R2/endpoint.json" ] && break; sleep 0.05; done
PIDS=""
for k in $(seq 1 40); do timeout 60 "$AOT" client "$R2" 30 > /tmp/si$k.log 2>&1 & PIDS="$PIDS $!"; done
for p in $PIDS; do wait $p; done
echo "hot:"; grep -l "transport=hot" /tmp/si*.log 2>/dev/null | wc -l
echo "file fallback:"; grep -l "transport=file" /tmp/si*.log 2>/dev/null | wc -l
tail -3 /tmp/si1.log
taskkill //F //IM hotdemo.exe > /dev/null 2>&1; true
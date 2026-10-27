AOT="F:/Courier/tools/hotdemo/out/aot/hotdemo.exe"
R="$TEMP/dup1"; rm -rf "$R"
echo "=== second host on the same root must fail loudly, not silently share ==="
"$AOT" host "$R" 2000000 > /tmp/dup1.log 2>&1 &
for i in $(seq 1 300); do [ -f "$R/endpoint.json" ] && break; sleep 0.05; done
sleep 0.3
timeout 10 "$AOT" host "$R" 2000000 > /tmp/dup2.log 2>&1
echo "second-host exit=$?"
head -4 /tmp/dup2.log
taskkill //F //IM hotdemo.exe > /dev/null 2>&1
sleep 0.4
echo
echo "=== file fallback: no hot field in endpoint -> client must use files ==="
R2="$TEMP/fb1"; rm -rf "$R2"; mkdir -p "$R2/commands" "$R2/results"
"$AOT" host-file "$R2" 50 > /tmp/fb1.log 2>&1 &
for i in $(seq 1 300); do [ -f "$R2/endpoint.json" ] && break; sleep 0.05; done
sleep 0.2
timeout 30 "$AOT" client "$R2" 5 2>&1 | tail -2
taskkill //F //IM hotdemo.exe > /dev/null 2>&1; true
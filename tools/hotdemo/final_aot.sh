set -u
AOT="F:/Courier/tools/hotdemo/out/aot/hotdemo.exe"
echo "=== selftest (AOT) ==="
"$AOT" selftest 2>&1 | tail -3
echo
echo "=== noop start (AOT, 10x) ==="
for i in $(seq 1 10); do S=$(date +%s%N); "$AOT" version > /dev/null 2>&1; E=$(date +%s%N); echo -n "$(( (E-S)/1000000 )) "; done; echo
rm -rf "$TEMP/hf9"
"$AOT" host "$TEMP/hf9" 2000000 > /tmp/hf9.log 2>&1 &
HJ=$!
for i in $(seq 1 300); do [ -f "$TEMP/hf9/endpoint.json" ] && break; sleep 0.05; done
sleep 0.3
echo "=== single-shot echo (AOT, 15x, new process each) ==="
for i in $(seq 1 15); do S=$(date +%s%N); "$AOT" echo "$TEMP/hf9" > /dev/null 2>&1; RC=$?; E=$(date +%s%N); echo -n "$(( (E-S)/1000000 ))(rc$RC) "; done; echo
echo "=== steady-state client (AOT, n=5000) ==="
"$AOT" client "$TEMP/hf9" 5000 2>&1 | tail -1
echo "=== steady-state client (AOT, second run, n=5000) ==="
"$AOT" client "$TEMP/hf9" 5000 2>&1 | tail -1
kill $HJ 2>/dev/null
taskkill //F //IM hotdemo.exe > /dev/null 2>&1; true
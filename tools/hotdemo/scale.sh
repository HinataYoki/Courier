AOT="F:/Courier/tools/hotdemo/out/aot/hotdemo.exe"
rm -rf "$TEMP/hs3"
"$AOT" host "$TEMP/hs3" 2000000 > /tmp/hs3.log 2>&1 &
HOSTJOB=$!
for i in $(seq 1 300); do [ -f "$TEMP/hs3/endpoint.json" ] && break; sleep 0.05; done
sleep 0.3
for N in 1 2 4 8; do
  PIDS=""
  S=$(date +%s%N)
  for k in $(seq 1 $N); do
    timeout 200 "$AOT" client "$TEMP/hs3" 1000 > /tmp/se$k.log 2>&1 &
    PIDS="$PIDS $!"
  done
  for p in $PIDS; do wait $p; done
  E=$(date +%s%N)
  MS=$(( (E-S)/1000000 ))
  echo "--- $N clients x 1000 msgs, wall=$MS ms, aggregate=$(( N*1000*1000/MS )) msg/s"
  for k in $(seq 1 $N); do tail -1 /tmp/se$k.log; done
done
kill $HOSTJOB 2>/dev/null
taskkill //F //IM hotdemo.exe > /dev/null 2>&1; true
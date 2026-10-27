set -u
AOT="F:/Courier/tools/hotdemo/out/aot/hotdemo.exe"
BASE="$TEMP/iso7"
rm -rf "$BASE"; mkdir -p "$BASE"
pidof_json() { sed -n 's/.*"pid": *\([0-9]*\).*/\1/p' "$1"; }

echo "########## TEST 1: 两个 root，两套 host/client ##########"
"$AOT" host "$BASE/a" 200000 > "$BASE/a.host.log" 2>&1 & HA=$!
"$AOT" host "$BASE/b" 200000 > "$BASE/b.host.log" 2>&1 & HB=$!
for i in $(seq 1 300); do [ -f "$BASE/a/endpoint.json" ] && [ -f "$BASE/b/endpoint.json" ] && break; sleep 0.05; done
echo "host A log: $(head -1 "$BASE/a.host.log")"
echo "host B log: $(head -1 "$BASE/b.host.log")"
"$AOT" client "$BASE/a" 2000 > "$BASE/a.cli.log" 2>&1 & CA=$!
"$AOT" client "$BASE/b" 2000 > "$BASE/b.cli.log" 2>&1 & CB=$!
wait $CA; wait $CB
echo "client A -> $(tail -1 "$BASE/a.cli.log")"
echo "client B -> $(tail -1 "$BASE/b.cli.log")"
echo
echo "--- 杀掉 host A，看 host B 是否受影响 ---"
taskkill //F //PID $(pidof_json "$BASE/a/endpoint.json") > /dev/null 2>&1
sleep 0.5
"$AOT" client "$BASE/b" 2000 > "$BASE/b2.cli.log" 2>&1
echo "host A 死后 client B -> $(tail -1 "$BASE/b2.cli.log")"
"$AOT" client "$BASE/a" 5 > "$BASE/a2.cli.log" 2>&1
echo "host A 死后 client A -> $(tail -1 "$BASE/a2.cli.log")"
taskkill //F //PID $(pidof_json "$BASE/b/endpoint.json") > /dev/null 2>&1
sleep 0.3

echo
echo "########## TEST 2: 同一个 root，两个 host ##########"
mkdir -p "$BASE/s"
"$AOT" host "$BASE/s" 200000 > "$BASE/s1.host.log" 2>&1 & S1=$!
for i in $(seq 1 300); do [ -f "$BASE/s/endpoint.json" ] && break; sleep 0.05; done
P1=$(pidof_json "$BASE/s/endpoint.json")
SID1=$(sed -n 's/.*"sessionId": *"\([0-9a-f]*\)".*/\1/p' "$BASE/s/endpoint.json")
echo "host#1 启动: pid=$P1 session=$SID1"
"$AOT" host "$BASE/s" 200000 > "$BASE/s2.host.log" 2>&1 & S2=$!
sleep 1.0
P2=$(pidof_json "$BASE/s/endpoint.json")
SID2=$(sed -n 's/.*"sessionId": *"\([0-9a-f]*\)".*/\1/p' "$BASE/s/endpoint.json")
echo "host#2 启动后 endpoint.json 变成: pid=$P2 session=$SID2"
echo "两个 host 都还活着吗: $(ps -W | grep -c hotdemo)"
"$AOT" client "$BASE/s" 1000 > "$BASE/s.cli.log" 2>&1
echo "client -> $(tail -1 "$BASE/s.cli.log")"
sleep 0.5
echo "host#1 处理数: $(cat "$BASE/s1.host.log" | tail -1)"
echo "host#2 处理数: $(cat "$BASE/s2.host.log" | tail -1)"
taskkill //F //IM hotdemo.exe > /dev/null 2>&1; true
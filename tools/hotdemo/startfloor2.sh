set -u
echo "=== baseline: 20 iterations of 2 date spawns, no work ==="
S=$(date +%s%N)
for i in $(seq 1 20); do A=$(date +%s%N); B=$(date +%s%N); done
E=$(date +%s%N)
echo "20 iters of bare date-pair: $(( (E-S)/1000000 )) ms total, $(( (E-S)/20000000 )) ms per date spawn"
echo
echo "=== AOT hotdemo version: 20 sequential in one loop, total wall ==="
S=$(date +%s%N); for i in $(seq 1 20); do /f/Courier/tools/hotdemo/out/aot/hotdemo.exe version > /dev/null 2>&1; done; E=$(date +%s%N)
echo "20 runs: $(( (E-S)/1000000 )) ms total, $(( (E-S)/20000000 )) ms each"
echo
echo "=== JIT hotdemo (dotnet apphost) version: 10 sequential ==="
S=$(date +%s%N); for i in $(seq 1 10); do /f/Courier/tools/hotdemo/bin/Release/net10.0/hotdemo.exe version > /dev/null 2>&1; done; E=$(date +%s%N)
echo "10 runs: $(( (E-S)/1000000 )) ms total, $(( (E-S)/10000000 )) ms each"
echo
echo "=== hostname.exe: 20 sequential in one loop ==="
S=$(date +%s%N); for i in $(seq 1 20); do /c/Windows/System32/hostname.exe > /dev/null 2>&1; done; E=$(date +%s%N)
echo "20 runs: $(( (E-S)/1000000 )) ms total, $(( (E-S)/20000000 )) ms each"
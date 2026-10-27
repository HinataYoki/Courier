set -u
echo "=== native small exe startup floor (hostname.exe), 20x ==="
for i in $(seq 1 20); do S=$(date +%s%N); /c/Windows/System32/hostname.exe > /dev/null 2>&1; E=$(date +%s%N); echo -n "$(( (E-S)/1000000 )) "; done; echo
echo "=== another native: whoami.exe, 10x ==="
for i in $(seq 1 10); do S=$(date +%s%N); /c/Windows/System32/whoami.exe > /dev/null 2>&1; E=$(date +%s%N); echo -n "$(( (E-S)/1000000 )) "; done; echo
echo "=== node (huge runtime) start, 5x ==="
for i in $(seq 1 5); do S=$(date +%s%N); node -e "" > /dev/null 2>&1; E=$(date +%s%N); echo -n "$(( (E-S)/1000000 )) "; done; echo
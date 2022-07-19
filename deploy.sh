#!/bin/bash

BASEPATH="$(dirname "$PWD")"

mkdir -p $BASEPATH/log
mkdir -p $BASEPATH/bin/syncer
mkdir -p $BASEPATH/bin/api
unzip -o NodeDBSyncer.zip -d $BASEPATH/bin/syncer
unzip -o WalletServer.zip -d $BASEPATH/bin/api
source ~/.bashrc

start() {
	NAME=$1
	PATH=$2
	BINARY=$3
	daemon --name=$NAME --restart
	if [ $? -ne 0 ]; then
		nohup daemon --name="$NAME" --respawn -f --output=$BASEPATH/log/${NAME}.log dotnet $BINARY >/dev/null 2>&1 &
		echo "$NAME started"
	else
		echo "$NAME restarted"
	fi
}

start pawket-api $BASEPATH/bin/api WalletServer.dll
start pawket-syncer $BASEPATH/bin/syncer NodeDBSyncer.dll
